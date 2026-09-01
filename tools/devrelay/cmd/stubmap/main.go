// Command stubmap is the "small stub map client that connects with
// role=map&code=… and prints frames" of PLAN.md §10. It is the quickest way to
// see what the mod is actually putting on the wire.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net/url"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/gorilla/websocket"
)

func main() {
	relayURL := flag.String("relay", "ws://localhost:8080/ws", "relay websocket URL")
	code := flag.String("code", "", "session code (required)")
	// §3.5 says a map sends request_state right after its welcome, so that is
	// the default here: welcome's roster carries no world block, and a map that
	// skips it does not know which world to draw until the 60 s hello heartbeat.
	requestState := flag.Bool("request-state", true, "send request_state after welcome (§3.5)")
	ping := flag.String("ping", "", "send a ping at x,z and exit after a moment")
	marker := flag.String("marker", "", "send a marker at x,z with an optional label: x,z[,label]")
	raw := flag.Bool("raw", false, "print frames verbatim rather than summarised")
	flag.Parse()

	if *code == "" {
		fmt.Fprintln(os.Stderr, "-code is required")
		os.Exit(2)
	}

	target, err := url.Parse(*relayURL)
	if err != nil {
		log.Fatalf("bad relay URL: %v", err)
	}
	query := target.Query()
	query.Set("role", "map")
	query.Set("code", *code)
	target.RawQuery = query.Encode()

	conn, resp, err := websocket.DefaultDialer.Dial(target.String(), nil)
	if err != nil {
		if resp != nil {
			log.Fatalf("dial failed: %v (HTTP %s)", err, resp.Status)
		}
		log.Fatalf("dial failed: %v", err)
	}
	defer conn.Close()

	done := make(chan struct{})
	go func() {
		defer close(done)
		for {
			_, payload, err := conn.ReadMessage()
			if err != nil {
				if code, text := closeInfo(err); code != 0 {
					fmt.Printf("closed: %d %s\n", code, text)
				}
				return
			}
			if *raw {
				fmt.Println(string(payload))
			} else {
				fmt.Println(summarise(payload))
			}
		}
	}()

	if *requestState {
		send(conn, map[string]any{"type": "request_state", "v": 1})
	}
	if *ping != "" {
		x, z := mustCoords(*ping)
		send(conn, map[string]any{"type": "ping", "v": 1, "x": x, "z": z, "name": "stubmap",
			"t": time.Now().UnixMilli()})
	}
	if *marker != "" {
		parts := strings.SplitN(*marker, ",", 3)
		x, z := mustCoords(strings.Join(parts[:2], ","))
		label := "from stubmap"
		if len(parts) == 3 {
			label = parts[2]
		}
		send(conn, map[string]any{"type": "marker", "v": 1, "op": "add",
			"id": "stubmap:" + strconv.FormatInt(time.Now().UnixNano(), 36),
			"x":  x, "z": z, "label": label, "icon": "dot", "t": time.Now().UnixMilli()})
	}

	<-done
}

func send(conn *websocket.Conn, frame map[string]any) {
	payload, err := json.Marshal(frame)
	if err != nil {
		log.Fatalf("encode: %v", err)
	}
	if err := conn.WriteMessage(websocket.TextMessage, payload); err != nil {
		log.Fatalf("send: %v", err)
	}
}

// summarise turns a frame into one readable line. Watching sixteen players at
// 1 Hz in raw JSON is unreadable, and the point of this tool is to be watched.
func summarise(payload []byte) string {
	var frame map[string]any
	if err := json.Unmarshal(payload, &frame); err != nil {
		return "?? " + string(payload)
	}

	id, _ := frame["playerId"].(string)
	if len(id) > 8 {
		id = id[:8]
	}

	switch frame["type"] {
	case "welcome":
		players, _ := frame["players"].([]any)
		return fmt.Sprintf("welcome  code=%v players=%d", frame["code"], len(players))
	case "hello":
		world, _ := frame["world"].(map[string]any)
		share := "sharing"
		if v, ok := frame["share"].(bool); ok && !v {
			share = "not sharing"
		}
		if world == nil {
			return fmt.Sprintf("hello    %s %v (%s)", id, frame["name"], share)
		}
		return fmt.Sprintf("hello    %s %v world=%v seed=%v (%s)",
			id, frame["name"], world["name"], world["seed"], share)
	case "position":
		extra := ""
		if hp, ok := frame["hp"]; ok {
			extra = fmt.Sprintf(" hp=%v/%v", hp, frame["maxHp"])
		}
		if dead, ok := frame["dead"].(bool); ok && dead {
			extra += " DEAD"
		}
		return fmt.Sprintf("position %s x=%-9v z=%-9v rot=%-6v %v%s",
			id, frame["x"], frame["z"], frame["rot"], frame["biome"], extra)
	case "ping":
		return fmt.Sprintf("ping     %s x=%v z=%v %v", id, frame["x"], frame["z"], frame["name"])
	case "marker":
		if frame["op"] == "remove" {
			return fmt.Sprintf("marker   %s remove %v", id, frame["id"])
		}
		return fmt.Sprintf("marker   %s add %v x=%v z=%v %q [%v]",
			id, frame["id"], frame["x"], frame["z"], frame["label"], frame["icon"])
	case "player_joined":
		return fmt.Sprintf("+ joined %s", id)
	case "player_left":
		return fmt.Sprintf("- left   %s", id)
	default:
		return fmt.Sprintf("%-8v %s", frame["type"], string(payload))
	}
}

func mustCoords(s string) (float64, float64) {
	parts := strings.SplitN(s, ",", 2)
	if len(parts) != 2 {
		log.Fatalf("expected x,z, got %q", s)
	}
	x, err := strconv.ParseFloat(strings.TrimSpace(parts[0]), 64)
	if err != nil {
		log.Fatalf("bad x: %v", err)
	}
	z, err := strconv.ParseFloat(strings.TrimSpace(parts[1]), 64)
	if err != nil {
		log.Fatalf("bad z: %v", err)
	}
	return x, z
}

func closeInfo(err error) (int, string) {
	var closeErr *websocket.CloseError
	if ok := asCloseError(err, &closeErr); ok {
		return closeErr.Code, closeErr.Text
	}
	return 0, ""
}

func asCloseError(err error, target **websocket.CloseError) bool {
	if ce, ok := err.(*websocket.CloseError); ok {
		*target = ce
		return true
	}
	return false
}
