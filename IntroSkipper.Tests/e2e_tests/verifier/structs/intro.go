package structs

type Intro struct {
	EpisodeId string

	Series string
	Season int
	Title  string

	IntroStart float32
	IntroEnd   float32
	Duration   float32
	Valid      bool

	FormattedStart string
	FormattedEnd   string

	ShowSkipPromptAt float32
	HideSkipPromptAt float32
}
