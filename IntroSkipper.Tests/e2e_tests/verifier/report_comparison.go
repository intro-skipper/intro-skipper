package main

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"html/template"
	"math"
	"os"
	"time"

	"github.com/confusedpolarbear/intro_skipper_verifier/structs"
)

//go:embed report.html
var reportTemplate []byte

func compareReports(oldReportPath, newReportPath, destination string) {
	start := time.Now()

	// Populate the destination filename if none was provided
	if destination == "" {
		destination = fmt.Sprintf("report-%d.html", start.Unix())
	}

	// Open the report for writing
	f, err := os.OpenFile(destination, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0600)
	if err != nil {
		panic(err)
	} else {
		defer f.Close()
	}

	fmt.Printf("Started at:    %s\n", start.Format(time.RFC1123))
	fmt.Printf("First report:  %s\n", oldReportPath)
	fmt.Printf("Second report: %s\n", newReportPath)
	fmt.Printf("Destination:   %s\n\n", destination)

	// Unmarshal both reports
	oldReport, newReport := unmarshalReport(oldReportPath), unmarshalReport(newReportPath)

	fmt.Println("[+] Comparing reports")

	// Setup a function map with helper functions to use in the template
	tmp := template.New("report")

	funcs := make(template.FuncMap)

	funcs["printTime"] = func(t time.Time) string {
		return t.Format(time.RFC1123)
	}

	funcs["printDuration"] = func(d time.Duration) string {
		return d.Round(time.Second).String()
	}

	funcs["printAnalysisSettings"] = func(pc structs.PluginConfiguration) string {
		return pc.AnalysisSettings()
	}

	funcs["printIntroductionReqs"] = func(pc structs.PluginConfiguration) string {
		return pc.IntroductionRequirements()
	}

	funcs["sortShows"] = templateSortShows
	funcs["sortSeasons"] = templateSortSeason
	funcs["compareEpisodes"] = templateCompareEpisodes
	tmp.Funcs(funcs)

	// Load the template or panic
	report := template.Must(tmp.Parse(string(reportTemplate)))

	err = report.Execute(f,
		structs.TemplateReportData{
			OldReport: oldReport,
			NewReport: newReport,
		})

	if err != nil {
		panic(err)
	}

	// Log success
	fmt.Printf("[+] Reports successfully compared in %s\n", time.Since(start).Round(time.Millisecond))
}

func unmarshalReport(path string) structs.Report {
	// Read the provided report
	contents, err := os.ReadFile(path)
	if err != nil {
		panic(err)
	}

	// Unmarshal
	var report structs.Report
	if err := json.Unmarshal(contents, &report); err != nil {
		panic(err)
	}

	// Setup maps and template data for later use
	report.Path = path
	report.Shows = make(map[string]structs.Seasons)
	report.IntroMap = make(map[string]structs.Intro)

	// Sort episodes by show and season
	for _, intro := range report.Intros {
		// Round the duration to the nearest second to avoid showing 8 decimal places in the report
		intro.Duration = float32(math.Round(float64(intro.Duration)))

		// Pretty print the intro start and end times
		intro.FormattedStart = (time.Duration(intro.IntroStart) * time.Second).String()
		intro.FormattedEnd = (time.Duration(intro.IntroEnd) * time.Second).String()

		show, season := intro.Series, intro.Season

		// If this show hasn't been seen before, allocate space for it
		if _, ok := report.Shows[show]; !ok {
			report.Shows[show] = make(structs.Seasons)
		}

		// Store this intro in the season of this show
		episodes := report.Shows[show][season]
		episodes = append(episodes, intro)
		report.Shows[show][season] = episodes

		// Store a reference to this intro in a lookup table
		report.IntroMap[intro.EpisodeId] = intro
	}

	// Print report info
	fmt.Printf("Report %s:\n", path)
	fmt.Printf("Generated with Jellyfin %s running on %s\n", report.ServerInfo.Version, report.ServerInfo.OperatingSystem)
	fmt.Printf("Analysis settings: %s\n", report.PluginConfig.AnalysisSettings())
	fmt.Printf("Introduction reqs: %s\n", report.PluginConfig.IntroductionRequirements())
	fmt.Printf("Episodes analyzed: %d\n", len(report.Intros))
	fmt.Println()

	return report
}
