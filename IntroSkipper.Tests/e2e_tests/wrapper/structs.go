package main

type Configuration struct {
	Common  Common   `json:"common"`
	Servers []Server `json:"servers"`
}

type Common struct {
	Library string `json:"library"`
	Episode string `json:"episode"`
}

type Server struct {
	Skip        bool     `json:"skip"`
	Comment     string   `json:"comment"`
	Address     string   `json:"address"`
	Image       string   `json:"image"`
	Username    string   `json:"username"`
	Password    string   `json:"password"`
	Browsers    []string `json:"browsers"`
	Tests       []string `json:"tests"`
	ManualTests bool     `json:"manual_tests"`

	// These properties are set at runtime
	Docker bool `json:"-"`
}
