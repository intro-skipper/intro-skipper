package main

import "testing"

func TestStringRedaction(t *testing.T) {
	raw := "-key deadbeef -first second -user admin -third fourth -pass hunter2"
	expected := "-key REDACTED -first second -user REDACTED -third fourth -pass REDACTED"
	actual := redactString(raw)

	if expected != actual {
		t.Errorf(`String was redacted incorrectly: "%s"`, actual)
	}
}
