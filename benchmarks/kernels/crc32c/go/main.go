// Command crc32c-go is the Go entry of the CRC-32C kernel: Go's standard library at its best,
// hash/crc32 with the Castagnoli table, timed with the kernel's protocol (the kernel's README).
//
// The benchmarks project's rules allow a language's standard library whatever it is written in,
// and Go's hash/crc32 runs its own hand-written Go assembly on amd64 (SSE4.2 crc32, three chains
// for long inputs). That is the fastest CRC-32C a Go program gets without third-party code, so it
// is Go's entry; the README says what it runs.
//
// The protocol: N goroutines, each locked to its own OS thread and checksumming its own copy of
// the input, wait at a start signal after a warm-up; the main goroutine opens the window, sleeps
// through it and raises the stop flag; each counts its calls and times its own window, and checks
// its checksum before and after. GOMAXPROCS is set to N. "Bytes allocated" is the runtime's
// TotalAlloc across the window, process-wide.
package main

import (
	"fmt"
	"hash/crc32"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// castagnoli is the CRC-32C table; MakeTable also selects the hardware path on amd64.
var castagnoli = crc32.MakeTable(crc32.Castagnoli)

// sink receives each goroutine's combined result, so no checksum can be proven unused.
var sink atomic.Uint64

// options is the protocol's command line.
type options struct {
	threads int             // worker goroutines, one per core the process may use
	inputs  string          // the folder holding <size>.bin
	sizes   []int           // input sizes in bytes, in order
	expect  []uint32        // each input's CRC-32C
	window  []time.Duration // each size's timed window
	warmup  []time.Duration // each size's warm-up
}

// stream is one goroutine's share of a cell.
type stream struct {
	calls   uint64  // checksums inside the window
	seconds float64 // from the start signal to the end of its last call
}

// parse reads the protocol's options; it returns an error naming the first bad one.
func parse(args []string) (options, error) {
	var o options
	values := map[string]string{}
	for i := 0; i+1 < len(args); i += 2 {
		values[args[i]] = args[i+1]
	}
	need := func(name string) (string, error) {
		v, ok := values[name]
		if !ok {
			return "", fmt.Errorf("%s is required", name)
		}
		return v, nil
	}
	var err error
	var v string
	if v, err = need("--threads"); err != nil {
		return o, err
	}
	if o.threads, err = strconv.Atoi(v); err != nil || o.threads < 1 {
		return o, fmt.Errorf("--threads %q", v)
	}
	if o.inputs, err = need("--inputs"); err != nil {
		return o, err
	}
	list := func(name string, item func(string) error) error {
		v, err := need(name)
		if err != nil {
			return err
		}
		for _, s := range strings.Split(v, ",") {
			if err := item(strings.TrimSpace(s)); err != nil {
				return fmt.Errorf("%s: %v", name, err)
			}
		}
		return nil
	}
	if err = list("--sizes", func(s string) error { n, e := strconv.Atoi(s); o.sizes = append(o.sizes, n); return e }); err != nil {
		return o, err
	}
	if err = list("--expect", func(s string) error {
		n, e := strconv.ParseUint(strings.TrimPrefix(s, "0x"), 16, 32)
		o.expect = append(o.expect, uint32(n))
		return e
	}); err != nil {
		return o, err
	}
	seconds := func(dst *[]time.Duration) func(string) error {
		return func(s string) error {
			f, e := strconv.ParseFloat(s, 64)
			*dst = append(*dst, time.Duration(f*float64(time.Second)))
			return e
		}
	}
	if err = list("--window", seconds(&o.window)); err != nil {
		return o, err
	}
	if err = list("--warmup", seconds(&o.warmup)); err != nil {
		return o, err
	}
	if len(o.expect) != len(o.sizes) || len(o.window) != len(o.sizes) || len(o.warmup) != len(o.sizes) {
		return o, fmt.Errorf("--sizes, --expect, --window and --warmup need one item per size")
	}
	return o, nil
}

// checksum is the entry's CRC-32C: one call into the standard library.
func checksum(b []byte) uint32 { return crc32.Checksum(b, castagnoli) }

// runCell times one cell and returns each goroutine's stream and the bytes allocated in the
// window, or an error when a checksum is wrong (no partial result).
func runCell(input []byte, expected uint32, threads int, window, warmup time.Duration) ([]stream, uint64, error) {
	batch := 65536 / len(input)
	if batch < 1 {
		batch = 1
	}
	streams := make([]stream, threads)
	failures := make([]error, threads)
	var ready, done sync.WaitGroup
	start := make(chan struct{})
	var stop atomic.Bool
	ready.Add(threads)
	done.Add(threads)
	for t := 0; t < threads; t++ {
		go func(index int) {
			defer done.Done()
			// One OS thread per goroutine for the whole cell: the scheduler cannot move a
			// checksum loop between threads, so each goroutine stays on the core it started on.
			runtime.LockOSThread()
			defer runtime.UnlockOSThread()
			own := append([]byte(nil), input...)
			if c := checksum(own); c != expected {
				failures[index] = fmt.Errorf("checksum 0x%08x, expected 0x%08x", c, expected)
				ready.Done()
				return
			}
			for end := time.Now().Add(warmup); time.Now().Before(end); {
				for i := 0; i < batch; i++ {
					checksum(own)
				}
			}
			ready.Done()
			<-start
			began := time.Now()
			var calls uint64
			var acc uint32
			// The flag is read once per batch: microseconds at 64 bytes, one call from 64 KiB up.
			for !stop.Load() {
				for i := 0; i < batch; i++ {
					acc ^= checksum(own)
				}
				calls += uint64(batch)
			}
			streams[index] = stream{calls: calls, seconds: time.Since(began).Seconds()}
			sink.Add(uint64(acc))
			if c := checksum(own); c != expected {
				failures[index] = fmt.Errorf("checksum after the window 0x%08x, expected 0x%08x", c, expected)
			}
		}(t)
	}
	ready.Wait()
	// ReadMemStats stops the world, so it runs only outside the window: once every goroutine
	// is ready, and again once every one has stopped.
	var before, after runtime.MemStats
	runtime.ReadMemStats(&before)
	close(start)
	time.Sleep(window)
	stop.Store(true)
	done.Wait()
	runtime.ReadMemStats(&after)
	for _, f := range failures {
		if f != nil {
			return nil, 0, f
		}
	}
	return streams, after.TotalAlloc - before.TotalAlloc, nil
}

// main checks the check value, then runs every cell of the command line.
func main() {
	o, err := parse(os.Args[1:])
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	// The driver confines the process to o.threads cores after it starts, possibly after the
	// runtime counted the machine's CPUs: set the goroutine slots to the cores it is given.
	runtime.GOMAXPROCS(o.threads)
	if c := checksum([]byte("123456789")); c != 0xE3069283 {
		fmt.Fprintf(os.Stderr, "check value: 0x%08x, expected 0xe3069283\n", c)
		os.Exit(1)
	}
	fmt.Printf("{\"type\":\"header\",\"entry\":\"go\",\"kernel\":\"hash/crc32 with the Castagnoli table (the standard library's SSE4.2 assembly)\",\"runtime\":\"%s\",\"threads\":%d,\"affinity\":\"%s\"}\n",
		runtime.Version(), o.threads, affinity())
	for i, size := range o.sizes {
		path := filepath.Join(o.inputs, fmt.Sprintf("%d.bin", size))
		input, err := os.ReadFile(path)
		if err != nil || len(input) != size {
			fmt.Fprintf(os.Stderr, "%s: %v (%d bytes, want %d)\n", path, err, len(input), size)
			os.Exit(1)
		}
		streams, allocated, err := runCell(input, o.expect[i], o.threads, o.window[i], o.warmup[i])
		if err != nil {
			fmt.Fprintf(os.Stderr, "%d bytes on %d threads: %v\n", size, o.threads, err)
			os.Exit(1)
		}
		var b strings.Builder
		fmt.Fprintf(&b, "{\"type\":\"cell\",\"size\":%d,\"threads\":%d,\"streams\":[", size, o.threads)
		for j, s := range streams {
			if j > 0 {
				b.WriteString(",")
			}
			fmt.Fprintf(&b, "{\"calls\":%d,\"seconds\":%.6f}", s.calls, s.seconds)
		}
		// The mask as of this cell, after its warm-up: the driver sets it right after starting the
		// process, so the header's reading can predate it, but a cell's never does.
		fmt.Fprintf(&b, "],\"allocatedBytes\":%d,\"affinity\":\"%s\"}", allocated, affinity())
		fmt.Println(b.String())
	}
}
