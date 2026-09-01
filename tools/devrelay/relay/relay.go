// Package relay is a development stand-in for the ValheimRelay WebSocket
// server. It implements the contract in PLAN.md §1 so the mod has something to
// integrate against locally. It is not the production relay — see README.md.
package relay

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

// Close codes (§1.4).
const (
	CloseTokenMismatch = 4003
	CloseUnknownCode   = 4004
	CloseRoomFull      = 4008
	CloseRelayFull     = 4013
)

// Options mirrors the relay's environment configuration (§1.5). Every limit is
// settable so a test can reach one without sixteen clients.
type Options struct {
	MaxMessageBytes int
	MaxModsPerRoom  int
	MaxMapsPerRoom  int
	MaxRooms        int
	RoomTTL         time.Duration
	ReclaimTTL      time.Duration
	PingInterval    time.Duration
	ReadDeadline    time.Duration
	Verbose         bool
}

// DefaultOptions are the defaults from the table in §1.5.
func DefaultOptions() Options {
	return Options{
		MaxMessageBytes: 8192,
		MaxModsPerRoom:  16,
		MaxMapsPerRoom:  8,
		MaxRooms:        1000,
		RoomTTL:         5 * time.Minute,
		ReclaimTTL:      30 * time.Minute,
		PingInterval:    54 * time.Second,
		ReadDeadline:    60 * time.Second,
	}
}

type role int

const (
	roleMod role = iota
	roleMap
)

type client struct {
	id       string
	role     role
	conn     *websocket.Conn
	out      chan []byte
	room     *room
	closeOne sync.Once

	mu   sync.Mutex
	name string
	uid  string
}

func (c *client) send(payload []byte) {
	select {
	case c.out <- payload:
	default:
		// A client that cannot keep up is dropped rather than allowed to
		// stall fan-out for everyone else in the room.
		c.close(websocket.CloseTryAgainLater, "send buffer full")
	}
}

func (c *client) close(code int, reason string) {
	c.closeOne.Do(func() {
		deadline := time.Now().Add(time.Second)
		msg := websocket.FormatCloseMessage(code, reason)
		_ = c.conn.WriteControl(websocket.CloseMessage, msg, deadline)
		_ = c.conn.Close()
	})
}

type room struct {
	code      string
	tokenHash string

	mu      sync.Mutex
	mods    map[string]*client
	maps    map[string]*client
	emptyAt time.Time
	sweptAt time.Time
}

// Relay is the server. The zero value is not usable; call New.
type Relay struct {
	opts Options

	mu    sync.Mutex
	rooms map[string]*room
}

// New builds a relay with the given options.
func New(opts Options) *Relay {
	return &Relay{opts: opts, rooms: make(map[string]*room)}
}

func hashToken(token string) string {
	// The real relay keeps "a code and a hash of its token" and nothing else
	// (§8); matching that here keeps the fixture honest about what it stores.
	sum := sha256.Sum256([]byte(token))
	return hex.EncodeToString(sum[:])
}

// ---------------------------------------------------------------- connections

var upgrader = websocket.Upgrader{
	ReadBufferSize:  4096,
	WriteBufferSize: 4096,
	// A browser map is a legitimate cross-origin client, and this fixture is
	// only ever reachable on localhost.
	CheckOrigin: func(*http.Request) bool { return true },
}

// ServeWS handles /ws.
func (r *Relay) ServeWS(w http.ResponseWriter, req *http.Request) {
	query := req.URL.Query()

	var cRole role
	switch query.Get("role") {
	case "mod":
		cRole = roleMod
	case "map":
		cRole = roleMap
	default:
		http.Error(w, "role must be mod or map", http.StatusBadRequest)
		return
	}

	rawCode := query.Get("code")
	token := query.Get("token")

	if cRole == roleMap && rawCode == "" {
		// Browsers never create rooms (§1.1).
		http.Error(w, "map role requires a code", http.StatusBadRequest)
		return
	}
	if token != "" && (cRole != roleMod || rawCode == "") {
		http.Error(w, "token is mod-only and always accompanies a code", http.StatusBadRequest)
		return
	}

	conn, err := upgrader.Upgrade(w, req, nil)
	if err != nil {
		return
	}
	conn.SetReadLimit(int64(r.opts.MaxMessageBytes))

	c := &client{
		id:   newPlayerID(),
		role: cRole,
		conn: conn,
		out:  make(chan []byte, 64),
	}

	rm, issuedToken, closeCode, err := r.resolveRoom(rawCode, token, cRole)
	if err != nil {
		c.close(closeCode, err.Error())
		return
	}
	c.room = rm

	if code, ok := rm.admit(c, r.opts); !ok {
		c.close(code, "room is full")
		return
	}

	go c.writePump(r.opts)
	r.writeWelcome(c, rm, issuedToken)

	if c.role == roleMod {
		rm.broadcastToMaps(mustJSON(map[string]any{
			"type": "player_joined", "playerId": c.id,
		}))
	}

	r.readPump(c)
}

func (r *Relay) resolveRoom(rawCode, token string, cRole role) (*room, string, int, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.sweepLocked()

	if rawCode == "" {
		// role=mod with no code: create (§1.1).
		if len(r.rooms) >= r.opts.MaxRooms {
			return nil, "", CloseRelayFull, errors.New("relay is at its room limit")
		}

		code, err := NewCode()
		if err != nil {
			return nil, "", websocket.CloseInternalServerErr, err
		}
		issued, err := NewToken()
		if err != nil {
			return nil, "", websocket.CloseInternalServerErr, err
		}

		rm := &room{
			code:      code,
			tokenHash: hashToken(issued),
			mods:      make(map[string]*client),
			maps:      make(map[string]*client),
		}
		r.rooms[code] = rm
		return rm, issued, 0, nil
	}

	code := NormaliseCode(rawCode)
	rm, ok := r.rooms[code]
	if !ok {
		return nil, "", CloseUnknownCode, errors.New("unknown or expired code")
	}

	if rm.isSwept() {
		// A swept room answers only to its token, for RECLAIM_TTL (§1.5).
		if token == "" {
			return nil, "", CloseUnknownCode, errors.New("unknown or expired code")
		}
		if hashToken(token) != rm.tokenHash {
			return nil, "", CloseTokenMismatch, errors.New("reclaim token does not match")
		}
		rm.revive()
		return rm, token, 0, nil
	}

	if token != "" {
		// Reclaiming a room that never expired: still verify, so a wrong token
		// is a clear 4003 rather than a silent join.
		if hashToken(token) != rm.tokenHash {
			return nil, "", CloseTokenMismatch, errors.New("reclaim token does not match")
		}
		return rm, token, 0, nil
	}

	return rm, "", 0, nil
}

func (r *Relay) writeWelcome(c *client, rm *room, token string) {
	welcome := map[string]any{
		"type":     "welcome",
		"code":     rm.code,
		"playerId": c.id,
		"players":  rm.roster(c.id),
	}
	if token != "" {
		welcome["token"] = token
	}
	c.send(mustJSON(welcome))
}

func (r *Relay) readPump(c *client) {
	defer r.disconnect(c)

	_ = c.conn.SetReadDeadline(time.Now().Add(r.opts.ReadDeadline))
	c.conn.SetPongHandler(func(string) error {
		return c.conn.SetReadDeadline(time.Now().Add(r.opts.ReadDeadline))
	})

	for {
		msgType, payload, err := c.conn.ReadMessage()
		if err != nil {
			return
		}
		if msgType != websocket.TextMessage {
			continue
		}
		_ = c.conn.SetReadDeadline(time.Now().Add(r.opts.ReadDeadline))

		if r.opts.Verbose {
			log.Printf("[%s %s] %s", c.room.code, c.id[:8], payload)
		}
		r.route(c, payload)
	}
}

// route applies the fan-out rules of §1.3.
func (r *Relay) route(c *client, payload []byte) {
	var frame map[string]any
	if err := json.Unmarshal(payload, &frame); err != nil {
		// The relay never parses the meaning of a frame, but it does have to
		// stamp playerId, so an unparseable frame from a mod cannot be routed.
		if c.role == roleMod {
			return
		}
		c.room.broadcastFromMap(c.id, payload)
		return
	}

	frameType, _ := frame["type"].(string)

	if c.role == roleMap {
		// Map frames are broadcast verbatim to every mod and to other maps.
		c.room.broadcastFromMap(c.id, payload)
		return
	}

	// playerId is overwritten on every frame from a mod; everything else passes
	// through untouched. A mod therefore cannot impersonate another player.
	frame["playerId"] = c.id

	if frameType == "hello" {
		name, _ := frame["name"].(string)
		uid, _ := frame["uid"].(string)
		c.mu.Lock()
		c.name, c.uid = name, uid
		c.mu.Unlock()
	}

	stamped := mustJSON(frame)

	// Mod frames always reach every map. They reach the other mods only when
	// type is exactly ping or marker: position telemetry would be noise in
	// another player's game, and it is the highest-rate traffic.
	c.room.broadcastToMaps(stamped)
	if frameType == "ping" || frameType == "marker" {
		c.room.broadcastToOtherMods(c.id, stamped)
	}
}

func (c *client) writePump(opts Options) {
	ticker := time.NewTicker(opts.PingInterval)
	defer ticker.Stop()

	for {
		select {
		case payload, ok := <-c.out:
			if !ok {
				return
			}
			_ = c.conn.SetWriteDeadline(time.Now().Add(10 * time.Second))
			if err := c.conn.WriteMessage(websocket.TextMessage, payload); err != nil {
				return
			}
		case <-ticker.C:
			// The control ping a client must answer, or the read deadline drops
			// it every minute (§4.2). Keeping it here is what lets the fixture
			// catch that failure locally instead of in someone's game.
			_ = c.conn.SetWriteDeadline(time.Now().Add(10 * time.Second))
			if err := c.conn.WriteMessage(websocket.PingMessage, nil); err != nil {
				return
			}
		}
	}
}

func (r *Relay) disconnect(c *client) {
	c.close(websocket.CloseNormalClosure, "")

	rm := c.room
	if rm == nil {
		return
	}

	rm.remove(c)
	if c.role == roleMod {
		rm.broadcastToMaps(mustJSON(map[string]any{
			"type": "player_left", "playerId": c.id,
		}))
	}
}

// ------------------------------------------------------------------ room state

func (rm *room) admit(c *client, opts Options) (int, bool) {
	rm.mu.Lock()
	defer rm.mu.Unlock()

	if c.role == roleMod {
		if len(rm.mods) >= opts.MaxModsPerRoom {
			return CloseRoomFull, false
		}
		rm.mods[c.id] = c
	} else {
		if len(rm.maps) >= opts.MaxMapsPerRoom {
			return CloseRoomFull, false
		}
		rm.maps[c.id] = c
	}

	rm.emptyAt = time.Time{}
	return 0, true
}

func (rm *room) remove(c *client) {
	rm.mu.Lock()
	defer rm.mu.Unlock()

	delete(rm.mods, c.id)
	delete(rm.maps, c.id)
	close(c.out)

	if len(rm.mods) == 0 && len(rm.maps) == 0 {
		rm.emptyAt = time.Now()
	}
}

func (rm *room) roster(exclude string) []map[string]any {
	rm.mu.Lock()
	defer rm.mu.Unlock()

	out := make([]map[string]any, 0, len(rm.mods))
	for id, peer := range rm.mods {
		if id == exclude {
			continue
		}
		peer.mu.Lock()
		entry := map[string]any{"playerId": id, "name": peer.name, "uid": peer.uid}
		peer.mu.Unlock()
		out = append(out, entry)
	}
	return out
}

func (rm *room) broadcastToMaps(payload []byte) {
	rm.mu.Lock()
	targets := make([]*client, 0, len(rm.maps))
	for _, m := range rm.maps {
		targets = append(targets, m)
	}
	rm.mu.Unlock()

	for _, t := range targets {
		t.send(payload)
	}
}

func (rm *room) broadcastToOtherMods(exclude string, payload []byte) {
	rm.mu.Lock()
	targets := make([]*client, 0, len(rm.mods))
	for id, m := range rm.mods {
		if id == exclude {
			continue
		}
		targets = append(targets, m)
	}
	rm.mu.Unlock()

	for _, t := range targets {
		t.send(payload)
	}
}

func (rm *room) broadcastFromMap(exclude string, payload []byte) {
	rm.mu.Lock()
	targets := make([]*client, 0, len(rm.mods)+len(rm.maps))
	for _, m := range rm.mods {
		targets = append(targets, m)
	}
	for id, m := range rm.maps {
		if id == exclude {
			continue
		}
		targets = append(targets, m)
	}
	rm.mu.Unlock()

	for _, t := range targets {
		t.send(payload)
	}
}

func (rm *room) isSwept() bool {
	rm.mu.Lock()
	defer rm.mu.Unlock()
	return !rm.sweptAt.IsZero()
}

func (rm *room) revive() {
	rm.mu.Lock()
	defer rm.mu.Unlock()
	rm.sweptAt = time.Time{}
	rm.emptyAt = time.Time{}
}

// sweepLocked applies ROOM_TTL and RECLAIM_TTL. Callers hold r.mu.
func (r *Relay) sweepLocked() {
	now := time.Now()
	for code, rm := range r.rooms {
		rm.mu.Lock()
		switch {
		case !rm.sweptAt.IsZero():
			// Already swept: it answers to its token until RECLAIM_TTL.
			if now.Sub(rm.sweptAt) >= r.opts.ReclaimTTL {
				rm.mu.Unlock()
				delete(r.rooms, code)
				continue
			}
		case !rm.emptyAt.IsZero() && now.Sub(rm.emptyAt) >= r.opts.RoomTTL:
			rm.sweptAt = now
		}
		rm.mu.Unlock()
	}
}

// Sweep runs the TTL pass. The server calls it on a ticker.
func (r *Relay) Sweep() {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.sweepLocked()
}

// RoomCount reports how many rooms exist, swept ones included. For tests.
func (r *Relay) RoomCount() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.rooms)
}

func mustJSON(v any) []byte {
	payload, err := json.Marshal(v)
	if err != nil {
		return []byte(`{"type":"error"}`)
	}
	return payload
}

func newPlayerID() string {
	buf := make([]byte, 16)
	if _, err := randRead(buf); err != nil {
		return "00000000-0000-0000-0000-000000000000"
	}
	buf[6] = (buf[6] & 0x0f) | 0x40
	buf[8] = (buf[8] & 0x3f) | 0x80
	h := hex.EncodeToString(buf)
	return h[0:8] + "-" + h[8:12] + "-" + h[12:16] + "-" + h[16:20] + "-" + h[20:]
}
