#!/bin/bash

echo "[+] Building timestamp verifier"
(cd verifier && go build -o verifier) || exit 1

echo "[+] Building test wrapper"
(cd wrapper && go test ./... && go build -o ../run_tests) || exit 1

echo
echo "[+] All programs built successfully"
