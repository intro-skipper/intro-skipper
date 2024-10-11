package structs

import (
	"fmt"
	"strings"
)

type PluginConfiguration struct {
	CacheFingerprints bool
	MaxParallelism    int
	SelectedLibraries string

	AnalysisPercent      int
	AnalysisLengthLimit  int
	MinimumIntroDuration int
}

func (c PluginConfiguration) AnalysisSettings() string {
	// If no libraries have been selected, display a star.
	// Otherwise, quote each library before displaying the slice.
	var libs []string
	if c.SelectedLibraries == "" {
		libs = []string{"*"}
	} else {
		for _, tmp := range strings.Split(c.SelectedLibraries, ",") {
			tmp = `"` + strings.TrimSpace(tmp) + `"`
			libs = append(libs, tmp)
		}
	}

	return fmt.Sprintf(
		"cfp=%t thr=%d lbs=%v",
		c.CacheFingerprints,
		c.MaxParallelism,
		libs)
}

func (c PluginConfiguration) IntroductionRequirements() string {
	return fmt.Sprintf(
		"per=%d%% max=%dm min=%ds",
		c.AnalysisPercent,
		c.AnalysisLengthLimit,
		c.MinimumIntroDuration)
}
