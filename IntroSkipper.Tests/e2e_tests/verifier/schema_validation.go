package main

import (
	"encoding/json"
	"fmt"
	"strings"
	"time"

	"github.com/confusedpolarbear/intro_skipper_verifier/structs"
)

// Given a comma separated list of item IDs, validate the returned API schema.
func validateApiSchema(hostAddress, apiKey, rawIds string) {
	// Iterate over the raw item IDs and validate the schema of API responses
	ids := strings.Split(rawIds, ",")

	start := time.Now()

	fmt.Printf("Started at:  %s\n", start.Format(time.RFC1123))
	fmt.Printf("Address:     %s\n", hostAddress)
	fmt.Println()

	// Get Jellyfin server information
	info := GetServerInfo(hostAddress, apiKey)
	fmt.Println()

	fmt.Printf("Jellyfin OS:      %s\n", info.OperatingSystem)
	fmt.Printf("Jellyfin version: %s\n", info.Version)
	fmt.Println()

	for _, id := range ids {
		fmt.Printf("[+] Validating item %s\n", id)

		fmt.Println("  [+] Validating API v1 (implicitly versioned)")
		intro, schema := getTimestampsV1(hostAddress, apiKey, id, "")
		validateV1Intro(id, intro, schema)

		fmt.Println("  [+] Validating API v1 (explicitly versioned)")
		intro, schema = getTimestampsV1(hostAddress, apiKey, id, "v1")
		validateV1Intro(id, intro, schema)

		fmt.Println()
	}

	fmt.Printf("Validated %d items in %s\n", len(ids), time.Since(start).Round(time.Millisecond))
}

// Validates the returned intro object, panicking on any error.
func validateV1Intro(id string, intro structs.Intro, schema map[string]interface{}) {
	// Validate the item ID
	if intro.EpisodeId != id {
		panic(fmt.Sprintf("Intro struct has incorrect item ID. Expected '%s', found '%s'", id, intro.EpisodeId))
	}

	// Validate the intro start and end times
	if intro.IntroStart < 0 || intro.IntroEnd < 0 {
		panic("Intro struct has a negative intro start or end time")
	}

	if intro.ShowSkipPromptAt > intro.IntroStart {
		panic("Intro struct show prompt time is after intro start")
	}

	if intro.HideSkipPromptAt > intro.IntroEnd {
		panic("Intro struct hide prompt time is after intro end")
	}

	// Validate the intro duration
	if duration := intro.IntroEnd - intro.IntroStart; duration < 15 {
		panic(fmt.Sprintf("Intro struct has duration %0.2f but the minimum allowed is 15", duration))
	}

	// Ensure the intro is marked as valid.
	if !intro.Valid {
		panic("Intro struct is not marked as valid")
	}

	// Check for any extraneous properties
	allowedProperties := []string{"EpisodeId", "Valid", "IntroStart", "IntroEnd", "ShowSkipPromptAt", "HideSkipPromptAt"}

	for schemaKey := range schema {
		okay := false

		for _, allowed := range allowedProperties {
			if allowed == schemaKey {
				okay = true
				break
			}
		}

		if !okay {
			panic(fmt.Sprintf("Intro object contains unknown key '%s'", schemaKey))
		}
	}
}

// Gets the timestamps for the provided item or panics.
func getTimestampsV1(hostAddress, apiKey, id, version string) (structs.Intro, map[string]interface{}) {
	var rawResponse map[string]interface{}
	var intro structs.Intro

	// Make an authenticated GET request to {Host}/Episode/{ItemId}/IntroTimestamps/{Version}
	raw := SendRequest("GET", fmt.Sprintf("%s/Episode/%s/IntroTimestamps/%s?hideUrl=1", hostAddress, id, version), apiKey)

	// Unmarshal the response as a version 1 API response, ignoring any unknown fields.
	if err := json.Unmarshal(raw, &intro); err != nil {
		panic(err)
	}

	// Second, unmarshal the response into a map so that any unknown fields can be detected and alerted on.
	if err := json.Unmarshal(raw, &rawResponse); err != nil {
		panic(err)
	}

	return intro, rawResponse
}
