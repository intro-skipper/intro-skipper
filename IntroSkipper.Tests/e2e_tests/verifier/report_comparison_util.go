package main

import (
	"fmt"
	"math"
	"sort"

	"github.com/confusedpolarbear/intro_skipper_verifier/structs"
)

// report template helper functions

// Sort show names alphabetically
func templateSortShows(shows map[string]structs.Seasons) []string {
	var showNames []string

	for show := range shows {
		showNames = append(showNames, show)
	}

	sort.Strings(showNames)

	return showNames
}

// Sort season numbers
func templateSortSeason(show structs.Seasons) []int {
	var keys []int

	for season := range show {
		keys = append(keys, season)
	}

	sort.Ints(keys)

	return keys
}

// Compare the episode with the provided ID in the old report to the episode in the new report.
func templateCompareEpisodes(id string, reports structs.TemplateReportData) structs.IntroPair {
	var pair structs.IntroPair
	var tolerance int = 5

	// Locate both episodes
	pair.Old = reports.OldReport.IntroMap[id]
	pair.New = reports.NewReport.IntroMap[id]

	// Mark the timestamps as similar if they are within a few seconds of each other
	similar := func(oldTime, newTime float32) bool {
		diff := math.Abs(float64(newTime) - float64(oldTime))
		return diff <= float64(tolerance)
	}

	if pair.Old.Valid && !pair.New.Valid {
		// If an intro was found previously, but not now, flag it
		pair.WarningShort = "only_previous"
		pair.Warning = "Introduction found in previous report, but not the current one"

	} else if !pair.Old.Valid && pair.New.Valid {
		// If an intro was not found previously, but found now, flag it
		pair.WarningShort = "improvement"
		pair.Warning = "New introduction discovered"

	} else if !pair.Old.Valid && !pair.New.Valid {
		// If an intro has never been found for this episode
		pair.WarningShort = "missing"
		pair.Warning = "No introduction has ever been found for this episode"

	} else if !similar(pair.Old.IntroStart, pair.New.IntroStart) || !similar(pair.Old.IntroEnd, pair.New.IntroEnd) {
		// If the intro timestamps are too different, flag it
		pair.WarningShort = "different"
		pair.Warning = fmt.Sprintf("Timestamps differ by more than %d seconds", tolerance)

	} else {
		// No warning was generated
		pair.WarningShort = "okay"
		pair.Warning = "Okay"
	}

	return pair
}
