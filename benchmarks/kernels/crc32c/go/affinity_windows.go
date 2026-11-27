//go:build windows

package main

import (
	"fmt"
	"syscall"
	"unsafe"
)

// affinity returns the process's affinity mask in hex, so the driver can check it confined the
// process as asked; it uses kernel32 through the standard library's syscall package.
func affinity() string {
	proc := syscall.NewLazyDLL("kernel32.dll").NewProc("GetProcessAffinityMask")
	process, err := syscall.GetCurrentProcess()
	if err != nil {
		return "unknown"
	}
	var processMask, systemMask uintptr
	if ok, _, _ := proc.Call(uintptr(process), uintptr(unsafe.Pointer(&processMask)), uintptr(unsafe.Pointer(&systemMask))); ok == 0 {
		return "unknown"
	}
	return fmt.Sprintf("0x%x", processMask)
}
