package main

import (
	"crypto/rand"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"log"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

// Tunables (variables so tests can shorten them).
var (
	equipTime  = 300 * time.Second
	graceTime  = 180 * time.Second
	clockUnit  = time.Second // one "second" of the turn clock
	emptyTTL   = 10 * time.Minute
	doneTTL    = time.Hour
	maxRooms   = 200
	maxPerKey  = 3
	hashWindow = int64(256) // hash reports older than this many seqs are dropped
)

const (
	maxState = 64 << 20  // state messages
	maxSmall = 64 << 10 // everything else
)

type Settings struct {
	Squad     int    `json:"squad"`
	Budget    int    `json:"budget"`
	Map       string `json:"map"`
	MapSize   int    `json:"mapSize"`
	Clock     int    `json:"clock"`
	TurnLimit int    `json:"turnLimit"`
	Blaster   bool   `json:"blaster"`
	Public    bool   `json:"public"`
	Hash      string `json:"hash"`
}

// client is one connection (WebSocket or dev TCP). out is drained by a writer goroutine.
type client struct {
	out  chan []byte
	kill func()
}

func newClient(kill func()) *client { return &client{out: make(chan []byte, 64), kill: kill} }

type room struct {
	id        string
	s         Settings
	keys      [2]string
	conn      [2]*client
	gone      [2]time.Time
	phase     string // wait, equip, battle, done
	created   time.Time
	empty     time.Time // when the last seat disconnected (zero while someone is connected)
	doneAt    time.Time
	loadout   [2]*string
	equipEnd  time.Time
	seq       int64
	turn      int
	clockGen  int
	hashes    map[int64]*[2]map[string]string
	wantState [2]bool
	result    [2]string
	// Both seats gone (#8): a seat that joins in battle has no state until it gets one (fresh). When both are
	// fresh the relay restores them itself from the newest state blob it has seen (or the start message) plus
	// the actions after it. ponytail: keeps one blob (up to 8 MB) and the action tail per room in memory.
	fresh    [2]bool
	startMsg []byte
	snapSeq  int64
	snap     *string
	acts     [][]byte // relayed act messages after snapSeq
}

// ponytail: one global lock for all rooms; per-room locks if 200 rooms ever contend.
var (
	mu    sync.Mutex
	rooms = map[string]*room{}
)

func randU64() uint64 {
	var b [8]byte
	rand.Read(b[:])
	return binary.LittleEndian.Uint64(b[:])
}

func randHex(n int) string {
	b := make([]byte, n)
	rand.Read(b)
	return hex.EncodeToString(b)
}

func send(c *client, v any) {
	if c == nil {
		return
	}
	b, _ := json.Marshal(v)
	select {
	case c.out <- b:
	default:
		log.Printf("slow reader, closing: %.80s", b)
		c.kill() // slow reader
	}
}

func (r *room) bcast(v any) { send(r.conn[0], v); send(r.conn[1], v) }

func (c *client) sendRaw(b []byte) {
	if c == nil {
		return
	}
	select {
	case c.out <- b:
	default:
		log.Printf("slow reader, closing: %.80s", b)
		c.kill()
	}
}

// newRoom creates a room with the creator in seat 0. Returns "" when a cap is hit.
func newRoom(s Settings, key string) string {
	mu.Lock()
	defer mu.Unlock()
	if len(rooms) >= maxRooms {
		return ""
	}
	n := 0
	for _, r := range rooms {
		if r.keys[0] == key && r.phase != "done" {
			n++
		}
	}
	if n >= maxPerKey {
		return ""
	}
	r := &room{id: randHex(6), s: s, phase: "wait", created: time.Now(), hashes: map[int64]*[2]map[string]string{}}
	r.empty = r.created
	r.keys[0] = key
	rooms[r.id] = r
	return r.id
}

// newDevRoom pre-creates a room without an owner (-devroom "<id>" or "<id>=<settings JSON>").
func newDevRoom(spec string) error {
	id, js, _ := strings.Cut(spec, "=")
	s := Settings{Squad: 6, Budget: 300000, Map: "random", MapSize: 40, Clock: 180}
	if js != "" {
		if err := json.Unmarshal([]byte(js), &s); err != nil {
			return err
		}
	}
	mu.Lock()
	defer mu.Unlock()
	r := &room{id: id, s: s, phase: "wait", created: time.Now(), hashes: map[int64]*[2]map[string]string{}}
	r.empty = r.created
	rooms[id] = r
	return nil
}

type msg struct {
	T       string            `json:"t"`
	Keep    bool              `json:"keep"`
	Hash    string            `json:"hash"`
	Build   string            `json:"build"`
	Data    *string           `json:"data"`
	A       json.RawMessage   `json:"a"`
	Seq     *int64            `json:"seq"`
	H       map[string]string `json:"h"`
	Outcome string            `json:"outcome"`
}

type errMsg struct {
	T   string `json:"t"`
	Msg string `json:"msg"`
}

func fail(m string) errMsg { return errMsg{"error", m} }

// join attaches c to room id under key after a valid hello. Returns seat or -1 (an error was sent).
func join(id, key string, c *client, hello msg) (*room, int) {
	mu.Lock()
	defer mu.Unlock()
	r := rooms[id]
	if r == nil {
		if len(rooms) >= maxRooms {
			send(c, fail("server full"))
			return nil, -1
		}
		r = &room{id: id, s: Settings{Squad: 6}, phase: "wait", created: time.Now(), hashes: map[int64]*[2]map[string]string{}}
		r.empty = r.created
		rooms[id] = r
	}
	if len(hello.Hash) > 128 || len(hello.Build) > 128 {
		send(c, fail("bad hello"))
		return nil, -1
	}
	if r.s.Hash != "" && hello.Hash != r.s.Hash {
		send(c, fail("hash mismatch"))
		return nil, -1
	}
	seat := -1
	for i, k := range r.keys {
		if k == key {
			seat = i
		}
	}
	if seat < 0 && r.keys[0] == "" {
		seat = 0 // dev room: no creator
		r.keys[0] = key
	}
	if seat < 0 {
		if r.keys[1] != "" {
			send(c, fail("room full"))
			return nil, -1
		}
		seat = 1
		r.keys[1] = key
	}
	if r.s.Hash == "" {
		r.s.Hash = hello.Hash // room created before the creator imported assets
	}
	if old := r.conn[seat]; old != nil {
		old.kill() // same key in a second tab: newest wins
	}
	r.conn[seat] = c
	r.empty = time.Time{}
	peer := r.conn[1-seat] != nil
	send(c, map[string]any{"t": "welcome", "seat": seat, "settings": r.s, "phase": r.phase, "peer": peer})
	if peer {
		send(r.conn[1-seat], map[string]any{"t": "peer", "connected": true, "grace": 0})
	}
	switch r.phase {
	case "wait":
		if r.conn[0] != nil && r.conn[1] != nil {
			r.phase = "equip"
			r.equipEnd = time.Now().Add(equipTime)
			r.bcast(map[string]any{"t": "equip", "left": int(equipTime / time.Second)})
			time.AfterFunc(equipTime, func() { r.equipExpired() })
		}
	case "equip":
		send(c, map[string]any{"t": "equip", "left": int(time.Until(r.equipEnd) / time.Second)})
	case "battle":
		r.fresh[seat] = true
		if peer && !r.fresh[1-seat] {
			r.wantState[seat] = true
			send(r.conn[1-seat], map[string]string{"t": "state_req"})
			r.startClock()
		} else if peer {
			r.restore()
			r.startClock()
		}
	case "done":
		send(c, r.overMsg())
	}
	return r, seat
}

func (r *room) leave(seat int, c *client) {
	mu.Lock()
	defer mu.Unlock()
	if r.conn[seat] != c {
		return
	}
	r.conn[seat] = nil
	r.gone[seat] = time.Now()
	r.wantState[seat] = false
	r.clockGen++ // battle pauses
	send(r.conn[1-seat], map[string]any{"t": "peer", "connected": false, "grace": int(graceTime / time.Second)})
	if r.phase == "battle" && r.conn[1-seat] != nil && !r.fresh[1-seat] {
		// keep a copy of the paused battle in case the other seat leaves too (#8)
		send(r.conn[1-seat], map[string]any{"t": "state_req", "keep": true})
	}
	if r.conn[1-seat] == nil {
		r.empty = time.Now()
	}
}

func (r *room) equipExpired() {
	mu.Lock()
	defer mu.Unlock()
	if r.phase != "equip" {
		return
	}
	// The seat that has not sent a loadout forfeits; both missing = draw.
	switch {
	case r.loadout[0] == nil && r.loadout[1] == nil:
		r.finish("forfeit", -1)
	case r.loadout[0] == nil:
		r.finish("forfeit", 1)
	default:
		r.finish("forfeit", 0)
	}
}

// startClock (re)starts the turn clock for r.turn and announces it.
func (r *room) startClock() {
	r.clockGen++
	gen := r.clockGen
	r.bcast(map[string]any{"t": "clock", "seat": r.turn, "left": r.s.Clock})
	if r.s.Clock > 0 {
		time.AfterFunc(time.Duration(r.s.Clock)*clockUnit, func() {
			mu.Lock()
			defer mu.Unlock()
			if gen == r.clockGen && r.phase == "battle" {
				r.pushAct(r.turn, json.RawMessage(`{"k":"endturn","timeout":true}`), "endturn")
			}
		})
	}
}

// restore sends both (fresh) seats the newest state the relay has seen, or the start message, and the actions after it.
func (r *room) restore() {
	log.Printf("room %s: both seats returned, restoring from seq %d", r.id, r.snapSeq)
	for i := range r.conn {
		if r.snap != nil {
			send(r.conn[i], map[string]any{"t": "state", "seq": r.snapSeq, "data": *r.snap})
		} else {
			r.conn[i].sendRaw(r.startMsg)
		}
		for _, b := range r.acts {
			r.conn[i].sendRaw(b)
		}
	}
	r.fresh = [2]bool{}
	r.wantState = [2]bool{}
	r.hashes = map[int64]*[2]map[string]string{}
}

func (r *room) pushAct(seat int, a json.RawMessage, k string) {
	r.seq++
	b, _ := json.Marshal(map[string]any{"t": "act", "seq": r.seq, "seat": seat, "seed": strconv.FormatUint(randU64(), 10), "a": a})
	r.acts = append(r.acts, b)
	r.conn[0].sendRaw(b)
	r.conn[1].sendRaw(b)
	switch k {
	case "endturn":
		r.turn = 1 - r.turn
		r.startClock()
	case "surrender":
		r.finish("surrender", 1-seat)
	}
}

func (r *room) overMsg() map[string]any {
	return map[string]any{"t": "over", "reason": r.result[0], "winner": r.winnerOf()}
}

// finish ends the match. reason/winner are kept in result[0]/result[1] after done.
func (r *room) finish(reason string, winner int) {
	r.phase = "done"
	r.doneAt = time.Now()
	r.clockGen++
	r.result = [2]string{reason, strconv.Itoa(winner)}
	r.bcast(r.overMsg())
}

func (r *room) winnerOf() int { w, _ := strconv.Atoi(r.result[1]); return w }

// handle processes one message from seat. Returns false to close the connection.
func (r *room) handle(seat int, raw []byte) bool {
	var m msg
	if json.Unmarshal(raw, &m) != nil || (m.T != "state" && len(raw) > maxSmall) {
		log.Printf("room %s seat %d: bad message %.80q", r.id, seat, raw)
		send(r.conn[seat], fail("bad message"))
		return false
	}
	mu.Lock()
	defer mu.Unlock()
	c := r.conn[seat]
	if c == nil {
		return false
	}
	other := 1 - seat
	switch m.T {
	case "loadout":
		if r.phase != "equip" || m.Data == nil || r.loadout[seat] != nil {
			return true
		}
		r.loadout[seat] = m.Data
		if r.loadout[other] != nil {
			r.phase = "battle"
			r.turn = 0
			r.startMsg, _ = json.Marshal(map[string]any{"t": "start", "seed": strconv.FormatUint(randU64(), 10), "first": r.turn,
				"loadouts": []string{*r.loadout[0], *r.loadout[1]}})
			r.conn[0].sendRaw(r.startMsg)
			r.conn[1].sendRaw(r.startMsg)
			r.startClock()
		}
	case "act":
		var a map[string]json.RawMessage
		var k string
		if json.Unmarshal(m.A, &a) != nil || a == nil || json.Unmarshal(a["k"], &k) != nil || k == "" || len(k) > 32 {
			send(c, fail("bad action"))
			return false
		}
		if r.phase != "battle" || r.conn[other] == nil && k != "surrender" || seat != r.turn && k != "surrender" {
			return true // off turn or paused: dropped
		}
		delete(a, "timeout") // only the server may mark timeouts
		b, _ := json.Marshal(a)
		r.pushAct(seat, b, k)
	case "hash":
		if m.Seq == nil || len(m.H) == 0 || len(m.H) > 32 {
			send(c, fail("bad hash"))
			return false
		}
		n := *m.Seq
		if r.phase != "battle" || n > r.seq || n < r.seq-hashWindow || r.wantState[0] || r.wantState[1] {
			return true // a state transfer is pending: hashes are compared again after it
		}
		for s := range r.hashes {
			if s < r.seq-hashWindow {
				delete(r.hashes, s)
			}
		}
		p := r.hashes[n]
		if p == nil {
			p = &[2]map[string]string{}
			r.hashes[n] = p
		}
		p[seat] = m.H
		if p[other] == nil {
			return true
		}
		delete(r.hashes, n)
		if diff := diffBuckets(p[0], p[1]); len(diff) > 0 {
			log.Printf("room %s: desync at seq %d %v", r.id, n, diff)
			r.bcast(map[string]any{"t": "desync", "seq": n, "buckets": diff})
			r.wantState[1-r.turn] = true
			send(r.conn[r.turn], map[string]string{"t": "state_req"})
			r.hashes = map[int64]*[2]map[string]string{}
		}
	case "state":
		if m.Seq == nil || m.Data == nil {
			send(c, fail("bad state"))
			return false
		}
		if *m.Seq >= r.snapSeq && *m.Seq <= r.seq {
			r.snapSeq, r.snap = *m.Seq, m.Data
			if k := int(r.seq - r.snapSeq); k < len(r.acts) {
				r.acts = r.acts[len(r.acts)-k:] // acts are contiguous up to r.seq: keep those after the blob
			}
		}
		if m.Keep {
			return true
		}
		if r.conn[other] != nil {
			r.wantState[other] = false
			r.fresh[other] = false
			r.hashes = map[int64]*[2]map[string]string{} // both sides continue from this state
			send(r.conn[other], map[string]any{"t": "state", "seq": *m.Seq, "data": *m.Data})
		}
	case "result":
		if m.Outcome != "win" && m.Outcome != "lose" && m.Outcome != "draw" {
			send(c, fail("bad result"))
			return false
		}
		if r.phase != "battle" {
			return true
		}
		switch m.Outcome {
		case "win":
			r.finish("elim", seat)
		case "lose":
			r.finish("elim", other)
		default:
			r.finish("draw", -1)
		}
	case "reject":
		// the opponent's loadout failed this client's validateLoadout (#6): nobody can play this match
		if r.phase == "battle" && r.seq == 0 {
			r.finish("rejected", -1)
		}
	case "claim":
		if (r.phase == "equip" || r.phase == "battle") && r.conn[other] == nil && r.keys[other] != "" &&
			time.Since(r.gone[other]) >= graceTime {
			r.finish("claim", seat)
		}
	case "hello":
		// duplicate hello: ignore
	default:
		send(c, fail("unknown message"))
		return false
	}
	return true
}

func diffBuckets(a, b map[string]string) []string {
	var d []string
	for k, v := range a {
		if w, ok := b[k]; !ok || w != v {
			d = append(d, k)
		}
	}
	for k := range b {
		if _, ok := a[k]; !ok {
			d = append(d, k)
		}
	}
	sort.Strings(d)
	return d
}

// expire drops idle rooms; run periodically.
func expire(now time.Time) {
	mu.Lock()
	defer mu.Unlock()
	for id, r := range rooms {
		if r.phase == "done" && now.Sub(r.doneAt) > doneTTL || !r.empty.IsZero() && now.Sub(r.empty) > emptyTTL {
			r.clockGen++
			delete(rooms, id)
		}
	}
}
