package main

import (
	"bytes"
	_ "embed"
	"fmt"
	"net/http"
)

//go:embed library.json
var librarySetupPayload string

func SetupServer(server, password string) {
	makeUrl := func(u string) string {
		return fmt.Sprintf("%s/%s", server, u)
	}

	// Set the server language to English
	sendRequest(
		makeUrl("Startup/Configuration"),
		"POST",
		`{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}`)

	// Get the first user
	sendRequest(makeUrl("Startup/User"), "GET", "")

	// Create the first user
	sendRequest(
		makeUrl("Startup/User"),
		"POST",
		fmt.Sprintf(`{"Name":"admin","Password":"%s"}`, password))

	// Create a TV library from the media at /media/TV.
	sendRequest(
		makeUrl("Library/VirtualFolders?collectionType=tvshows&refreshLibrary=false&name=Shows"),
		"POST",
		librarySetupPayload)

	// Setup remote access
	sendRequest(
		makeUrl("Startup/RemoteAccess"),
		"POST",
		`{"EnableRemoteAccess":true,"EnableAutomaticPortMapping":false}`)

	// Mark the wizard as complete
	sendRequest(
		makeUrl("Startup/Complete"),
		"POST",
		``)
}

func sendRequest(url string, method string, body string) {
	// Create the request
	req, err := http.NewRequest(method, url, bytes.NewBuffer([]byte(body)))
	if err != nil {
		panic(err)
	}

	// Set required headers
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set(
		"X-Emby-Authorization",
		`MediaBrowser Client="JF E2E Tests", Version="0.0.1", DeviceId="E2E", Device="E2E"`)

	// Send it
	fmt.Printf("  [+] %s %s", method, url)
	res, err := http.DefaultClient.Do(req)

	if err != nil {
		fmt.Println()
		panic(err)
	}

	fmt.Printf(" %d\n", res.StatusCode)

	if res.StatusCode != http.StatusNoContent && res.StatusCode != http.StatusOK {
		panic("invalid status code received during setup")
	}
}
