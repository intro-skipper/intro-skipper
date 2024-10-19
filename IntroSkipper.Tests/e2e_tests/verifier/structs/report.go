package structs

import "time"

type Seasons map[int][]Intro

type Report struct {
	Path string `json:"-"`

	StartedAt  time.Time
	FinishedAt time.Time
	Runtime    time.Duration

	ServerInfo   PublicInfo
	PluginConfig PluginConfiguration

	Intros []Intro

	// Intro lookup table. Only populated when loading a report.
	IntroMap map[string]Intro `json:"-"`

	// Intros which have been sorted by show and season number. Only populated when loading a report.
	Shows map[string]Seasons `json:"-"`
}

// Data passed to the report template.
type TemplateReportData struct {
	// First report.
	OldReport Report

	// Second report.
	NewReport Report
}

// A pair of introductions from an old and new reports.
type IntroPair struct {
	Old Intro
	New Intro

	// Recognized warning types:
	//   * okay:          no warning
	//   * different:     timestamps are too dissimilar
	//   * only_previous: introduction found in old report but not new one
	WarningShort string

	// If this pair of intros is not okay, a short description about the cause
	Warning string
}
