//go:build !windows

package main

// affinity returns "unknown" outside Windows: the benchmark runs on Windows, where the driver
// sets and checks the process's affinity mask.
func affinity() string { return "unknown" }
