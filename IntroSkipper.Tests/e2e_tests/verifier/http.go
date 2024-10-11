package main

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"

	"github.com/confusedpolarbear/intro_skipper_verifier/structs"
)

// Gets the contents of the provided URL or panics.
func SendRequest(method, url, apiKey string) []byte {
	http.DefaultClient.Timeout = 10 * time.Second

	// Construct the request
	req, err := http.NewRequest(method, url, nil)
	if err != nil {
		panic(err)
	}

	// Include the authorization token
	req.Header.Set("Authorization", fmt.Sprintf(`MediaBrowser Token="%s"`, apiKey))

	// Send the request
	res, err := http.DefaultClient.Do(req)

	if !strings.Contains(url, "hideUrl") {
		fmt.Printf("[+] %s %s: %d\n", method, url, res.StatusCode)
	}

	// Panic if any error occurred
	if err != nil {
		panic(err)
	}

	// Check for API key validity
	if res.StatusCode == http.StatusUnauthorized {
		panic("Server returned 401 (Unauthorized). Check API key validity and try again.")
	}

	// Read and return the entire body
	defer res.Body.Close()
	body, err := io.ReadAll(res.Body)
	if err != nil {
		panic(err)
	}

	return body
}

func GetServerInfo(hostAddress, apiKey string) structs.PublicInfo {
	var info structs.PublicInfo

	fmt.Println("[+] Getting server information")
	rawInfo := SendRequest("GET", hostAddress+"/System/Info/Public", apiKey)

	if err := json.Unmarshal(rawInfo, &info); err != nil {
		panic(err)
	}

	return info
}

func GetPluginConfiguration(hostAddress, apiKey string) structs.PluginConfiguration {
	var config structs.PluginConfiguration

	fmt.Println("[+] Getting plugin configuration")
	rawConfig := SendRequest("GET", hostAddress+"/Plugins/c83d86bb-a1e0-4c35-a113-e2101cf4ee6b/Configuration", apiKey)

	if err := json.Unmarshal(rawConfig, &config); err != nil {
		panic(err)
	}

	return config
}
