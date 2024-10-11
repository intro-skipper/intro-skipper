package main

import (
	"flag"
	"time"
)

func flags() {
	// Report generation
	hostAddress := flag.String("address", "", "Address of Jellyfin server to extract intro information from.")
	apiKey := flag.String("key", "", "Administrator API key to authenticate with.")
	keepTimestamps := flag.Bool("keep", false, "Keep the current timestamps instead of erasing and reanalyzing.")
	pollInterval := flag.Duration("poll", 10*time.Second, "Interval to poll task completion at.")
	reportDestination := flag.String("o", "", "Report destination filename. Defaults to intros-ADDRESS-TIMESTAMP.json.")

	// Report comparison
	report1 := flag.String("r1", "", "First report.")
	report2 := flag.String("r2", "", "Second report.")

	// API schema validator
	ids := flag.String("validate", "", "Comma separated item ids to validate the API schema for.")

	// Print usage examples
	flag.CommandLine.Usage = func() {
		flag.CommandLine.Output().Write([]byte("Flags:\n"))
		flag.PrintDefaults()

		usage := "\nUsage:\n" +
			"Generate intro timestamp report from a local server:\n" +
			"./verifier -address http://127.0.0.1:8096 -key api_key\n\n" +

			"Generate intro timestamp report from a remote server, polling for task completion every 20 seconds:\n" +
			"./verifier -address https://example.com -key api_key -poll 20s -o example.json\n\n" +

			"Compare two previously generated reports:\n" +
			"./verifier -r1 v0.1.5.json -r2 v0.1.6.json\n\n" +

			"Validate the API schema for some item ids:\n" +
			"./verifier -address http://127.0.0.1:8096 -key api_key -validate id1,id2,id3\n"

		flag.CommandLine.Output().Write([]byte(usage))
	}

	flag.Parse()

	if *hostAddress != "" && *apiKey != "" {
		if *ids == "" {
			generateReport(*hostAddress, *apiKey, *reportDestination, *keepTimestamps, *pollInterval)
		} else {
			validateApiSchema(*hostAddress, *apiKey, *ids)
		}

	} else if *report1 != "" && *report2 != "" {
		compareReports(*report1, *report2, *reportDestination)

	} else {
		panic("Either (-address and -key) or (-r1 and -r2) are required.")
	}
}

func main() {
	flags()
}
