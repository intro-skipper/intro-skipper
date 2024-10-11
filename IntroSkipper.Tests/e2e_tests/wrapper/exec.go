package main

import (
	"bufio"
	"context"
	"fmt"
	"io"
	"os/exec"
	"regexp"
	"strings"
	"time"
)

// Run an external program
func RunProgram(program string, args []string, timeout time.Duration) {
	// Flag if we are starting or stopping a container
	managingContainer := program == "docker"

	// Create context and command
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()
	cmd := exec.CommandContext(ctx, program, args...)

	// Stringify and censor the program's arguments
	strArgs := redactString(strings.Join(args, " "))
	fmt.Printf("  [+] Running %s %s\n", program, strArgs)

	// Setup pipes
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		panic(err)
	}

	stderr, err := cmd.StderrPipe()
	if err != nil {
		panic(err)
	}

	// Start the command
	if err := cmd.Start(); err != nil {
		panic(err)
	}

	// Stream any messages to the terminal
	for _, r := range []io.Reader{stdout, stderr} {
		// Don't log stdout from the container
		if managingContainer && r == stdout {
			continue
		}

		scanner := bufio.NewScanner(r)
		scanner.Split(bufio.ScanRunes)

		for scanner.Scan() {
			fmt.Print(scanner.Text())
		}
	}
}

// Redacts sensitive command line arguments.
func redactString(raw string) string {
	redactionRegex := regexp.MustCompilePOSIX(`-(user|pass|key) [^ ]+`)
	return redactionRegex.ReplaceAllString(raw, "-$1 REDACTED")
}
