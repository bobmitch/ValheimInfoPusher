// Command devrelay runs the development relay fixture. See ../../README.md —
// this is not the production relay.
package main

import (
	"flag"
	"log"
	"net"
	"net/http"
	"time"

	"valheimrelay.dev/devrelay/relay"
)

func main() {
	opts := relay.DefaultOptions()
	addr := flag.String("addr", ":8080", "listen address")
	flag.IntVar(&opts.MaxModsPerRoom, "max-mods", opts.MaxModsPerRoom, "MAX_MODS_PER_ROOM")
	flag.IntVar(&opts.MaxMapsPerRoom, "max-maps", opts.MaxMapsPerRoom, "MAX_MAPS_PER_ROOM")
	flag.IntVar(&opts.MaxRooms, "max-rooms", opts.MaxRooms, "MAX_ROOMS")
	flag.IntVar(&opts.MaxMessageBytes, "max-message-bytes", opts.MaxMessageBytes, "MAX_MESSAGE_BYTES")
	flag.DurationVar(&opts.RoomTTL, "room-ttl", opts.RoomTTL, "ROOM_TTL")
	flag.DurationVar(&opts.ReclaimTTL, "reclaim-ttl", opts.ReclaimTTL, "RECLAIM_TTL")
	flag.DurationVar(&opts.PingInterval, "ping-interval", opts.PingInterval, "websocket control ping interval")
	flag.DurationVar(&opts.ReadDeadline, "read-deadline", opts.ReadDeadline, "read deadline; a client that does not answer pings is dropped")
	flag.BoolVar(&opts.Verbose, "v", false, "log every frame")
	flag.Parse()

	r := relay.New(opts)

	go func() {
		for range time.Tick(5 * time.Second) {
			r.Sweep()
		}
	}()

	mux := http.NewServeMux()
	mux.HandleFunc("/ws", r.ServeWS)
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok\n"))
	})

	server := &http.Server{
		Addr:              *addr,
		Handler:           mux,
		ReadHeaderTimeout: 10 * time.Second,
	}

	listener, err := net.Listen("tcp", *addr)
	if err != nil {
		log.Fatalf("listen: %v", err)
	}

	// Printed in a fixed, parseable form so a test harness can start the relay
	// on :0 and discover the port it actually got.
	log.Printf("devrelay listening on ws://%s/ws — development fixture, not the production relay", listener.Addr())
	log.Fatal(server.Serve(listener))
}
