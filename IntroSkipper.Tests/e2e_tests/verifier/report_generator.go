package main

import (
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"strings"
	"time"

	"github.com/confusedpolarbear/intro_skipper_verifier/structs"
)

var spinners []string
var spinnerIndex int

func generateReport(hostAddress, apiKey, reportDestination string, keepTimestamps bool, pollInterval time.Duration) {
	start := time.Now()

	// Setup the spinner
	spinners = strings.Split("⣷⣯⣟⡿⢿⣻⣽⣾", "")
	spinnerIndex = -1 // start the spinner on the first graphic

	// Setup the filename to save intros to
	if reportDestination == "" {
		reportDestination = fmt.Sprintf("intros-%s-%d.json", hostAddress, time.Now().Unix())
		reportDestination = strings.ReplaceAll(reportDestination, "http://", "")
		reportDestination = strings.ReplaceAll(reportDestination, "https://", "")
	}

	// Ensure the file is writable
	if err := os.WriteFile(reportDestination, nil, 0600); err != nil {
		panic(err)
	}

	fmt.Printf("Started at:  %s\n", start.Format(time.RFC1123))
	fmt.Printf("Address:     %s\n", hostAddress)
	fmt.Printf("Destination: %s\n", reportDestination)
	fmt.Println()

	// Get Jellyfin server information and plugin configuration
	info := GetServerInfo(hostAddress, apiKey)
	config := GetPluginConfiguration(hostAddress, apiKey)
	fmt.Println()

	fmt.Printf("Jellyfin OS:       %s\n", info.OperatingSystem)
	fmt.Printf("Jellyfin version:  %s\n", info.Version)
	fmt.Printf("Analysis settings: %s\n", config.AnalysisSettings())
	fmt.Printf("Introduction reqs: %s\n", config.IntroductionRequirements())
	fmt.Printf("Erase timestamps:  %t\n", !keepTimestamps)
	fmt.Println()

	// If not keeping timestamps, run the fingerprint task.
	// Otherwise, log that the task isn't being run
	if !keepTimestamps {
		runAnalysisAndWait(hostAddress, apiKey, pollInterval)
	} else {
		fmt.Println("[+] Using previously discovered intros")
	}
	fmt.Println()

	// Save all intros from the server
	fmt.Println("[+] Saving intros")

	var report structs.Report
	rawIntros := SendRequest("GET", hostAddress+"/Intros/All", apiKey)
	if err := json.Unmarshal(rawIntros, &report.Intros); err != nil {
		panic(err)
	}

	// Calculate the durations of all intros
	for i := range report.Intros {
		intro := report.Intros[i]
		intro.Duration = intro.IntroEnd - intro.IntroStart
		report.Intros[i] = intro
	}

	fmt.Println()
	fmt.Println("[+] Saving report")

	// Store timing data, server information, and plugin configuration
	report.StartedAt = start
	report.FinishedAt = time.Now()
	report.Runtime = report.FinishedAt.Sub(report.StartedAt)
	report.ServerInfo = info
	report.PluginConfig = config

	// Marshal the report
	marshalled, err := json.Marshal(report)
	if err != nil {
		panic(err)
	}

	if err := os.WriteFile(reportDestination, marshalled, 0600); err != nil {
		panic(err)
	}

	// Change report permissions
	exec.Command("chown", "1000:1000", reportDestination).Run()

	fmt.Println("[+] Done")
}

func runAnalysisAndWait(hostAddress, apiKey string, pollInterval time.Duration) {
	var taskId string = ""

	type taskInfo struct {
		State                     string
		CurrentProgressPercentage int
	}

	fmt.Println("[+] Erasing previously discovered intros")
	SendRequest("POST", hostAddress+"/Intros/EraseTimestamps", apiKey)
	fmt.Println()

	var taskIds = []string{
		"f64d8ad58e3d7b98548e1a07697eb100", // v0.1.8
		"8863329048cc357f7dfebf080f2fe204",
		"6adda26c5261c40e8fa4a7e7df568be2"}

	fmt.Println("[+] Starting analysis task")
	for _, id := range taskIds {
		body := SendRequest("POST", hostAddress+"/ScheduledTasks/Running/"+id, apiKey)
		fmt.Println()

		// If the scheduled task was found, store the task ID for later
		if !strings.Contains(string(body), "Not Found") {
			taskId = id
			break
		}
	}

	if taskId == "" {
		panic("unable to find scheduled task")
	}

	fmt.Println("[+] Waiting for analysis task to complete")
	fmt.Print("[+] Episodes analyzed: 0%")

	var info taskInfo       // Last known scheduled task state
	var lastQuery time.Time // Time the task info was last updated

	for {
		time.Sleep(500 * time.Millisecond)

		// Update the spinner
		if spinnerIndex++; spinnerIndex >= len(spinners) {
			spinnerIndex = 0
		}

		fmt.Printf("\r[%s] Episodes analyzed: %d%%", spinners[spinnerIndex], info.CurrentProgressPercentage)

		if info.CurrentProgressPercentage == 100 {
			fmt.Printf("\r[+]") // reset the spinner
			fmt.Println()
			break
		}

		// Get the latest task state & unmarshal (only if enough time has passed since the last update)
		if time.Since(lastQuery) <= pollInterval {
			continue
		}

		lastQuery = time.Now()

		raw := SendRequest("GET", hostAddress+"/ScheduledTasks/"+taskId+"?hideUrl=1", apiKey)

		if err := json.Unmarshal(raw, &info); err != nil {
			fmt.Printf("[!] Unable to unmarshal response into taskInfo struct: %s\n", err)
			fmt.Printf("%s\n", raw)
			continue
		}

		// Print the latest task state
		switch info.State {
		case "Idle":
			info.CurrentProgressPercentage = 100
		}
	}
}
