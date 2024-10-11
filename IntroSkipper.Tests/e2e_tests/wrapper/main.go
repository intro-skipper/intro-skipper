package main

import (
	"bufio"
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"path"
	"strings"
	"time"
)

// IP address to use when connecting to local containers.
var containerAddress string

// Path to compiled plugin DLL to install in local containers.
var pluginPath string

// Randomly generated password used to setup container with.
var containerPassword string

func flags() {
	flag.StringVar(&pluginPath, "dll", "", "Path to plugin DLL to install in container images.")
	flag.StringVar(&containerAddress, "caddr", "", "IP address to use when connecting to local containers.")
	flag.Parse()

	// Randomize the container's password
	rawPassword := make([]byte, 32)
	if _, err := rand.Read(rawPassword); err != nil {
		panic(err)
	}

	containerPassword = hex.EncodeToString(rawPassword)
}

func main() {
	flags()

	start := time.Now()

	fmt.Printf("[+] Start time: %s\n", start)

	// Load list of servers
	fmt.Println("[+] Loading configuration")
	config := loadConfiguration()
	fmt.Println()

	// Start Selenium by bringing up the compose file in detatched mode
	fmt.Println("[+] Starting Selenium")
	RunProgram("docker-compose", []string{"up", "-d"}, 10*time.Second)

	// If any error occurs, bring Selenium down before exiting
	defer func() {
		fmt.Println("[+] Stopping Selenium")
		RunProgram("docker-compose", []string{"down"}, 15*time.Second)
	}()

	// Test all provided Jellyfin servers
	for _, server := range config.Servers {
		if server.Skip {
			continue
		}

		var configurationDirectory string
		var apiKey string
		var seleniumArgs []string

		// LSIO containers use some slighly different paths & permissions
		lsioImage := strings.Contains(server.Image, "linuxserver")

		fmt.Println()
		fmt.Printf("[+] Testing %s\n", server.Comment)

		if server.Docker {
			var err error

			// Setup a temporary folder for the container's configuration
			configurationDirectory, err = os.MkdirTemp("/dev/shm", "jf-e2e-*")
			if err != nil {
				panic(err)
			}

			// Create a folder to install the plugin into
			pluginDirectory := path.Join(configurationDirectory, "plugins", "intro-skipper")
			if lsioImage {
				pluginDirectory = path.Join(configurationDirectory, "data", "plugins", "intro-skipper")
			}

			fmt.Println("  [+] Creating plugin directory")
			if err := os.MkdirAll(pluginDirectory, 0700); err != nil {
				fmt.Printf("  [!] Failed to create plugin directory: %s\n", err)
				goto cleanup
			}

			// If this is an LSIO container, adjust the permissions on the plugin directory
			if lsioImage {
				RunProgram(
					"chown",
					[]string{
						"911:911",
						"-R",
						path.Join(configurationDirectory, "data", "plugins")},
					2*time.Second)
			}

			// Install the plugin
			fmt.Printf("  [+] Copying plugin %s to %s\n", pluginPath, pluginDirectory)
			RunProgram("cp", []string{pluginPath, pluginDirectory}, 2*time.Second)
			fmt.Println()

			/* Start the container with the following settings:
			 *    Name:  jf-e2e
			 *    Port:  8097
			 *    Media: Mounted to /media, read only
			 */
			containerArgs := []string{"run", "--name", "jf-e2e", "--rm", "-p", "8097:8096",
				"-v", fmt.Sprintf("%s:%s:rw", configurationDirectory, "/config"),
				"-v", fmt.Sprintf("%s:%s:ro", config.Common.Library, "/media"),
				server.Image}

			fmt.Printf("  [+] Starting container %s\n", server.Image)
			go RunProgram("docker", containerArgs, 60*time.Second)

			// Wait for the container to fully start
			waitForServerStartup(server.Address)
			fmt.Println()

			fmt.Println("  [+] Setting up container")

			// Set up the container
			SetupServer(server.Address, containerPassword)

			// Restart the container and wait for it to come back up
			RunProgram("docker", []string{"restart", "jf-e2e"}, 10*time.Second)
			time.Sleep(time.Second)
			waitForServerStartup(server.Address)
			fmt.Println()
		} else {
			fmt.Println("[+] Remote instance, assuming plugin is already installed")
		}

		// Get an API key
		apiKey = login(server)

		// Rescan the library if this is a server that we just setup
		if server.Docker {
			fmt.Println("  [+] Rescanning library")

			sendRequest(
				server.Address+"/ScheduledTasks/Running/7738148ffcd07979c7ceb148e06b3aed?api_key="+apiKey,
				"POST",
				"")

			// TODO: poll for task completion
			time.Sleep(10 * time.Second)

			fmt.Println()
		}

		// Analyze episodes and save report
		fmt.Println("  [+] Analyzing episodes")
		fmt.Print("\033[37;1m") // change the color of the verifier's text
		RunProgram(
			"./verifier/verifier",
			[]string{
				"-address", server.Address,
				"-key", apiKey, "-o",
				fmt.Sprintf("reports/%s-%d.json", server.Comment, start.Unix())},
			5*time.Minute)
		fmt.Print("\033[39;0m") // reset terminal text color

		// Pause for any manual tests
		if server.ManualTests {
			fmt.Println("  [!] Pausing for manual tests")
			reader := bufio.NewReader(os.Stdin)
			reader.ReadString('\n')
		}

		// Setup base Selenium arguments
		seleniumArgs = []string{
			"-u", // force stdout to be unbuffered
			"main.py",
			"-host", server.Address,
			"-user", server.Username,
			"-pass", server.Password,
			"-name", config.Common.Episode}

		// Append all requested Selenium tests
		seleniumArgs = append(seleniumArgs, "--tests")
		seleniumArgs = append(seleniumArgs, server.Tests...)

		// Append all requested browsers
		seleniumArgs = append(seleniumArgs, "--browsers")
		seleniumArgs = append(seleniumArgs, server.Browsers...)

		// Run Selenium
		os.Chdir("selenium")
		RunProgram("python3", seleniumArgs, time.Minute)
		os.Chdir("..")

	cleanup:
		if server.Docker {
			// Stop the container
			fmt.Println("  [+] Stopping and removing container")
			RunProgram("docker", []string{"stop", "jf-e2e"}, 10*time.Second)

			// Cleanup the container's configuration
			fmt.Printf("  [+] Deleting %s\n", configurationDirectory)

			if err := os.RemoveAll(configurationDirectory); err != nil {
				panic(err)
			}
		}
	}
}

// Login to the specified Jellyfin server and return an API key
func login(server Server) string {
	type AuthenticateUserByName struct {
		AccessToken string
	}

	fmt.Println("  [+] Sending authentication request")

	// Create request body
	rawBody := fmt.Sprintf(`{"Username":"%s","Pw":"%s"}`, server.Username, server.Password)
	body := bytes.NewBufferString(rawBody)

	// Create the request
	req, err := http.NewRequest(
		"POST",
		fmt.Sprintf("%s/Users/AuthenticateByName", server.Address),
		body)

	if err != nil {
		panic(err)
	}

	// Set headers
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set(
		"X-Emby-Authorization",
		`MediaBrowser Client="JF E2E Tests", Version="0.0.1", DeviceId="E2E", Device="E2E"`)

	// Authenticate
	res, err := http.DefaultClient.Do(req)
	if err != nil {
		panic(err)
	} else if res.StatusCode != http.StatusOK {
		panic(fmt.Sprintf("authentication returned code %d", res.StatusCode))
	}

	defer res.Body.Close()

	// Read body
	fullBody, err := io.ReadAll(res.Body)
	if err != nil {
		panic(err)
	}

	// Unmarshal body and return token
	var token AuthenticateUserByName
	if err := json.Unmarshal(fullBody, &token); err != nil {
		panic(err)
	}

	return token.AccessToken
}

// Wait up to ten seconds for the provided Jellyfin server to fully startup
func waitForServerStartup(address string) {
	attempts := 10
	fmt.Println("  [+] Waiting for server to finish starting")

	for {
		// Sleep in between requests
		time.Sleep(time.Second)

		// Ping the /System/Info/Public endpoint
		res, err := http.Get(fmt.Sprintf("%s/System/Info/Public", address))

		// If the server didn't return 200 OK, loop
		if err != nil || res.StatusCode != http.StatusOK {
			if attempts--; attempts <= 0 {
				panic("server is taking too long to startup")
			}

			continue
		}

		// Assume startup has finished, break
		break
	}
}

// Read configuration from config.json
func loadConfiguration() Configuration {
	var config Configuration

	// Load the contents of the configuration file
	raw, err := os.ReadFile("config.json")
	if err != nil {
		panic(err)
	}

	// Unmarshal
	if err := json.Unmarshal(raw, &config); err != nil {
		panic(err)
	}

	// Print debugging info
	fmt.Printf("Library:  %s\n", config.Common.Library)
	fmt.Printf("Episode:  \"%s\"\n", config.Common.Episode)
	fmt.Printf("Password: %s\n", containerPassword)
	fmt.Println()

	// Check the validity of all entries
	for i, server := range config.Servers {
		// If this is an entry for a local container, ensure the server address is correct
		if server.Image != "" {
			// Ensure that values were provided for the host's IP address, base configuration directory,
			// and a path to the compiled plugin DLL to install.
			if containerAddress == "" {
				panic("The -caddr argument is required.")
			}

			if pluginPath == "" {
				panic("The -dll argument is required.")
			}

			server.Username = "admin"
			server.Password = containerPassword
			server.Address = fmt.Sprintf("http://%s:8097", containerAddress)
			server.Docker = true
		}

		// If no browsers were specified, default to Chrome (for speed)
		if len(server.Browsers) == 0 {
			server.Browsers = []string{"chrome"}
		}

		// If no tests were specified, only test that the plugin settings page works
		if len(server.Tests) == 0 {
			server.Tests = []string{"settings"}
		}

		// Verify that an address was provided
		if len(server.Address) == 0 {
			panic("Server address is required")
		}

		fmt.Printf("===== Server: %s =====\n", server.Comment)

		if server.Skip {
			fmt.Println("Skip:     true")
		}

		fmt.Printf("Docker:   %t\n", server.Docker)
		if server.Docker {
			fmt.Printf("Image:    %s\n", server.Image)
		}

		fmt.Printf("Address:  %s\n", server.Address)
		fmt.Printf("Browsers: %v\n", server.Browsers)
		fmt.Printf("Tests:    %v\n", server.Tests)
		fmt.Println()

		config.Servers[i] = server
	}

	fmt.Println("=================")

	return config
}
