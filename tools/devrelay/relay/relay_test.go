package relay

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"
)

func newTestServer(t *testing.T, tune func(*Options)) (*httptest.Server, *Relay) {
	t.Helper()

	opts := DefaultOptions()
	// Short enough that a TTL test does not take five minutes.
	opts.RoomTTL = 50 * time.Millisecond
	opts.ReclaimTTL = 2 * time.Second
	if tune != nil {
		tune(&opts)
	}

	r := New(opts)
	mux := http.NewServeMux()
	mux.HandleFunc("/ws", r.ServeWS)
	server := httptest.NewServer(mux)
	t.Cleanup(server.Close)
	return server, r
}

type conn struct {
	t  *testing.T
	ws *websocket.Conn
}

func dial(t *testing.T, server *httptest.Server, query string) (*conn, *http.Response, error) {
	t.Helper()
	url := "ws" + strings.TrimPrefix(server.URL, "http") + "/ws?" + query
	ws, resp, err := websocket.DefaultDialer.Dial(url, nil)
	if err != nil {
		return nil, resp, err
	}
	t.Cleanup(func() { _ = ws.Close() })
	return &conn{t: t, ws: ws}, resp, nil
}

func mustDial(t *testing.T, server *httptest.Server, query string) *conn {
	t.Helper()
	c, _, err := dial(t, server, query)
	if err != nil {
		t.Fatalf("dial %q: %v", query, err)
	}
	return c
}

func (c *conn) read() map[string]any {
	c.t.Helper()
	_ = c.ws.SetReadDeadline(time.Now().Add(2 * time.Second))
	_, payload, err := c.ws.ReadMessage()
	if err != nil {
		c.t.Fatalf("read: %v", err)
	}
	var frame map[string]any
	if err := json.Unmarshal(payload, &frame); err != nil {
		c.t.Fatalf("decode %q: %v", payload, err)
	}
	return frame
}

// readType skips frames the test does not care about, such as player_joined.
func (c *conn) readType(want string) map[string]any {
	c.t.Helper()
	for i := 0; i < 10; i++ {
		frame := c.read()
		if frame["type"] == want {
			return frame
		}
	}
	c.t.Fatalf("never saw a %q frame", want)
	return nil
}

func (c *conn) send(frame map[string]any) {
	c.t.Helper()
	payload, err := json.Marshal(frame)
	if err != nil {
		c.t.Fatalf("encode: %v", err)
	}
	if err := c.ws.WriteMessage(websocket.TextMessage, payload); err != nil {
		c.t.Fatalf("send: %v", err)
	}
}

func (c *conn) expectNothing(d time.Duration) {
	c.t.Helper()
	_ = c.ws.SetReadDeadline(time.Now().Add(d))
	if _, payload, err := c.ws.ReadMessage(); err == nil {
		c.t.Fatalf("expected silence, got %s", payload)
	}
}

func (c *conn) expectClose(want int) {
	c.t.Helper()
	_ = c.ws.SetReadDeadline(time.Now().Add(2 * time.Second))
	for {
		_, _, err := c.ws.ReadMessage()
		if err == nil {
			continue
		}
		ce, ok := err.(*websocket.CloseError)
		if !ok {
			c.t.Fatalf("expected close %d, got %v", want, err)
		}
		if ce.Code != want {
			c.t.Fatalf("expected close %d, got %d (%s)", want, ce.Code, ce.Text)
		}
		return
	}
}

// createRoom runs the create path and returns the connection, code and token.
func createRoom(t *testing.T, server *httptest.Server) (*conn, string, string) {
	t.Helper()
	c := mustDial(t, server, "role=mod")
	welcome := c.readType("welcome")
	code, _ := welcome["code"].(string)
	token, _ := welcome["token"].(string)
	if code == "" || token == "" {
		t.Fatalf("create must yield a code and a token, got %v", welcome)
	}
	return c, code, token
}

// ------------------------------------------------------------------- §1.1/§1.2

func TestCreateIssuesACodeAndAToken(t *testing.T) {
	server, _ := newTestServer(t, nil)
	_, code, _ := createRoom(t, server)

	if len(code) != CodeLength {
		t.Fatalf("code %q is not %d characters", code, CodeLength)
	}
	if strings.ContainsAny(code, "ILOU") {
		t.Fatalf("code %q contains a character Crockford base32 excludes", code)
	}
}

func TestJoiningModsGetNoToken(t *testing.T) {
	server, _ := newTestServer(t, nil)
	_, code, _ := createRoom(t, server)

	joiner := mustDial(t, server, "role=mod&code="+code)
	welcome := joiner.readType("welcome")

	if _, ok := welcome["token"]; ok {
		t.Fatal("a joining mod must not receive a token")
	}
}

func TestCodesAreNormalisedForgivingly(t *testing.T) {
	server, _ := newTestServer(t, nil)
	_, code, _ := createRoom(t, server)

	// §1.1: case, spaces, hyphens and underscores are ignored. The mod passes
	// through whatever the player typed, so this has to hold.
	mangled := strings.ToLower(code[:4] + "-" + code[4:])
	joiner := mustDial(t, server, "role=mod&code="+mangled)

	if got := joiner.readType("welcome")["code"]; got != code {
		t.Fatalf("expected the canonical code %q, got %v", code, got)
	}
}

func TestConfusableCharactersFold(t *testing.T) {
	cases := map[string]string{
		"k7mq2xr4":      "K7MQ2XR4",
		"  ABCD-EFGH  ": "ABCDEFGH",
		"I1L0O":         "11100",
		"a_b c":         "ABC",
	}
	for input, want := range cases {
		if got := NormaliseCode(input); got != want {
			t.Errorf("NormaliseCode(%q) = %q, want %q", input, got, want)
		}
	}
}

func TestWelcomeCarriesTheRosterSoAMidSessionJoinerHasEveryone(t *testing.T) {
	server, _ := newTestServer(t, nil)
	creator, code, _ := createRoom(t, server)
	creator.send(map[string]any{"type": "hello", "name": "Bob", "uid": "vh_bob"})

	// Let the hello land before the second client asks for the roster.
	time.Sleep(50 * time.Millisecond)

	joiner := mustDial(t, server, "role=mod&code="+code)
	players, _ := joiner.readType("welcome")["players"].([]any)

	if len(players) != 1 {
		t.Fatalf("expected one peer in the roster, got %v", players)
	}
	peer, _ := players[0].(map[string]any)
	if peer["name"] != "Bob" || peer["uid"] != "vh_bob" {
		t.Fatalf("roster lost the hello fields: %v", peer)
	}
}

func TestPlayerIdIsFreshOnEveryConnection(t *testing.T) {
	server, _ := newTestServer(t, nil)
	_, code, _ := createRoom(t, server)

	first := mustDial(t, server, "role=mod&code="+code).readType("welcome")["playerId"]
	second := mustDial(t, server, "role=mod&code="+code).readType("welcome")["playerId"]

	if first == second {
		t.Fatal("playerId must not be stable across connections")
	}
}

func TestBrowsersCannotCreateRooms(t *testing.T) {
	server, _ := newTestServer(t, nil)
	_, resp, err := dial(t, server, "role=map")

	if err == nil {
		t.Fatal("a map with no code must be refused")
	}
	if resp == nil || resp.StatusCode != http.StatusBadRequest {
		t.Fatalf("expected 400, got %v", resp)
	}
}

// ----------------------------------------------------------------------- §1.3

func TestModFramesAlwaysReachMaps(t *testing.T) {
	server, _ := newTestServer(t, nil)
	mod, code, _ := createRoom(t, server)
	webmap := mustDial(t, server, "role=map&code="+code)
	webmap.readType("welcome")

	mod.send(map[string]any{"type": "position", "x": 1.5, "z": -2.5})

	frame := webmap.readType("position")
	if frame["x"] != 1.5 {
		t.Fatalf("position did not pass through: %v", frame)
	}
}

func TestPositionIsNotFannedOutToPeerMods(t *testing.T) {
	// §1.3: telemetry would be noise in another player's game, and it is the
	// highest-rate traffic.
	server, _ := newTestServer(t, nil)
	mod, code, _ := createRoom(t, server)
	peer := mustDial(t, server, "role=mod&code="+code)
	peer.readType("welcome")

	mod.send(map[string]any{"type": "position", "x": 1, "z": 2})
	peer.expectNothing(200 * time.Millisecond)
}

func TestPingAndMarkerReachPeerMods(t *testing.T) {
	server, _ := newTestServer(t, nil)
	mod, code, _ := createRoom(t, server)
	peer := mustDial(t, server, "role=mod&code="+code)
	peer.readType("welcome")

	mod.send(map[string]any{"type": "ping", "x": 1, "z": 2})
	if got := peer.readType("ping"); got["x"] != float64(1) {
		t.Fatalf("ping did not reach the peer intact: %v", got)
	}

	mod.send(map[string]any{"type": "marker", "op": "add", "id": "m1", "x": 3, "z": 4})
	if got := peer.readType("marker"); got["id"] != "m1" {
		t.Fatalf("marker did not reach the peer intact: %v", got)
	}
}

func TestPlayerIdIsOverwrittenSoAModCannotImpersonate(t *testing.T) {
	server, _ := newTestServer(t, nil)
	mod, code, _ := createRoom(t, server)
	webmap := mustDial(t, server, "role=map&code="+code)
	webmap.readType("welcome")

	mod.send(map[string]any{"type": "position", "playerId": "somebody-else", "x": 1, "z": 2})

	if got := webmap.readType("position")["playerId"]; got == "somebody-else" {
		t.Fatal("the relay must overwrite playerId on every mod frame")
	}
}

func TestEveryOtherFieldPassesThroughUntouched(t *testing.T) {
	server, _ := newTestServer(t, nil)
	mod, code, _ := createRoom(t, server)
	webmap := mustDial(t, server, "role=map&code="+code)
	webmap.readType("welcome")

	mod.send(map[string]any{
		"type": "position", "x": 1, "z": 2,
		"somethingTheRelayHasNeverHeardOf": "kept",
	})

	if got := webmap.readType("position")["somethingTheRelayHasNeverHeardOf"]; got != "kept" {
		t.Fatalf("unknown fields must pass through: %v", got)
	}
}

func TestMapFramesAreBroadcastToEveryMod(t *testing.T) {
	server, _ := newTestServer(t, nil)
	mod, code, _ := createRoom(t, server)
	webmap := mustDial(t, server, "role=map&code="+code)
	webmap.readType("welcome")

	webmap.send(map[string]any{"type": "request_state", "v": 1})

	if got := mod.readType("request_state"); got["v"] != float64(1) {
		t.Fatalf("request_state did not reach the mod: %v", got)
	}
}

func TestOnlyMapsSeeJoinAndLeaveEvents(t *testing.T) {
	server, _ := newTestServer(t, nil)
	mod, code, _ := createRoom(t, server)
	webmap := mustDial(t, server, "role=map&code="+code)
	webmap.readType("welcome")

	peer := mustDial(t, server, "role=mod&code="+code)
	peer.readType("welcome")

	if got := webmap.readType("player_joined"); got["playerId"] == nil {
		t.Fatalf("map should see player_joined: %v", got)
	}
	mod.expectNothing(200 * time.Millisecond)
}

// ----------------------------------------------------------------------- §1.4

func TestUnknownCodeIsRejectedWith4004(t *testing.T) {
	server, _ := newTestServer(t, nil)
	c := mustDial(t, server, "role=mod&code=ZZZZZZZZ")
	c.expectClose(CloseUnknownCode)
}

func TestWrongTokenIsRejectedWith4003(t *testing.T) {
	server, _ := newTestServer(t, nil)
	_, code, _ := createRoom(t, server)

	c := mustDial(t, server, "role=mod&code="+code+"&token=deadbeef")
	c.expectClose(CloseTokenMismatch)
}

func TestTheSeventeenthPlayerGets4008(t *testing.T) {
	server, _ := newTestServer(t, func(o *Options) { o.MaxModsPerRoom = 2 })
	_, code, _ := createRoom(t, server)

	second := mustDial(t, server, "role=mod&code="+code)
	second.readType("welcome")

	third := mustDial(t, server, "role=mod&code="+code)
	third.expectClose(CloseRoomFull)
}

func TestTheRoomLimitGives4013(t *testing.T) {
	server, _ := newTestServer(t, func(o *Options) { o.MaxRooms = 1 })
	createRoom(t, server)

	second := mustDial(t, server, "role=mod")
	second.expectClose(CloseRelayFull)
}

// ------------------------------------------------------------- §1.5 lifetimes

func TestARoomOutlivesItsLastClientSoABriefDropNeedsNoHandling(t *testing.T) {
	server, r := newTestServer(t, func(o *Options) { o.RoomTTL = time.Hour })
	creator, code, _ := createRoom(t, server)

	_ = creator.ws.Close()
	time.Sleep(50 * time.Millisecond)
	r.Sweep()

	// Reconnecting with the code alone resumes the same session (§1.5).
	rejoin := mustDial(t, server, "role=mod&code="+code)
	if got := rejoin.readType("welcome")["code"]; got != code {
		t.Fatalf("expected to resume %q, got %v", code, got)
	}
}

func TestASweptRoomAnswersOnlyToItsToken(t *testing.T) {
	server, r := newTestServer(t, nil) // RoomTTL is 50ms here
	creator, code, token := createRoom(t, server)

	_ = creator.ws.Close()
	time.Sleep(100 * time.Millisecond)
	r.Sweep()

	// The code alone is now 4004...
	bare := mustDial(t, server, "role=mod&code="+code)
	bare.expectClose(CloseUnknownCode)

	// ...but the token reclaims it, which is the whole point of §5.3.
	reclaimed := mustDial(t, server, "role=mod&code="+code+"&token="+token)
	welcome := reclaimed.readType("welcome")
	if welcome["code"] != code {
		t.Fatalf("reclaim returned the wrong code: %v", welcome)
	}
	if welcome["token"] != token {
		t.Fatal("a reclaiming mod must be handed its token back")
	}
}

func TestAReclaimedRoomIsUsableAgain(t *testing.T) {
	server, r := newTestServer(t, nil)
	creator, code, token := createRoom(t, server)
	_ = creator.ws.Close()
	time.Sleep(100 * time.Millisecond)
	r.Sweep()

	reclaimed := mustDial(t, server, "role=mod&code="+code+"&token="+token)
	reclaimed.readType("welcome")

	// A browser left open on the old code keeps working.
	webmap := mustDial(t, server, "role=map&code="+code)
	webmap.readType("welcome")
	reclaimed.send(map[string]any{"type": "position", "x": 7, "z": 8})

	if got := webmap.readType("position"); got["x"] != float64(7) {
		t.Fatalf("reclaimed room did not route: %v", got)
	}
}

func TestReclaimExpiresAfterReclaimTTL(t *testing.T) {
	server, r := newTestServer(t, func(o *Options) {
		o.RoomTTL = 10 * time.Millisecond
		o.ReclaimTTL = 50 * time.Millisecond
	})
	creator, code, token := createRoom(t, server)

	_ = creator.ws.Close()
	time.Sleep(30 * time.Millisecond)
	r.Sweep()
	time.Sleep(80 * time.Millisecond)
	r.Sweep()

	if r.RoomCount() != 0 {
		t.Fatal("the room should be gone after RECLAIM_TTL")
	}
	gone := mustDial(t, server, "role=mod&code="+code+"&token="+token)
	gone.expectClose(CloseUnknownCode)
}

func TestAnOversizedFrameIsRefused(t *testing.T) {
	// §1.5: MAX_MESSAGE_BYTES. The mod checks this itself so the failure is a
	// log line rather than a mysterious disconnect — this is the other half.
	server, _ := newTestServer(t, func(o *Options) { o.MaxMessageBytes = 256 })
	mod, _, _ := createRoom(t, server)

	mod.send(map[string]any{"type": "position", "label": strings.Repeat("x", 512)})

	_ = mod.ws.SetReadDeadline(time.Now().Add(2 * time.Second))
	if _, _, err := mod.ws.ReadMessage(); err == nil {
		t.Fatal("expected the connection to be dropped")
	}
}

func TestTheRelayStoresTheTokenOnlyAsAHash(t *testing.T) {
	// §8: the relay keeps nothing on disk and nothing survives a session except
	// a code and a hash of its token.
	server, _ := newTestServer(t, nil)
	_, _, token := createRoom(t, server)

	digest := hashToken(token)
	if digest == token {
		t.Fatal("the token must not be stored verbatim")
	}
	if !strings.Contains(digest, "") || len(digest) != 64 {
		t.Fatalf("expected a sha256 hex digest, got %q", digest)
	}
	if strings.Contains(digest, token) {
		t.Fatal("the digest must not contain the token")
	}
}
