// Command server is the PhoenixBattles relay: newline-delimited JSON over TCP, one room per invite code.
// Protocol (same as OXCE-Battles' dev TCP transport): first line {"t":"join","room":..,"key":..}, then
// hello -> welcome, equip, loadout -> start, act / state are forwarded to the other seat, result -> over.
package main

import (
	"bufio"
	"encoding/json"
	"flag"
	"log"
	"net"
	"time"
)

func main() {
	addr := flag.String("addr", ":8378", "TCP listen address")
	flag.Parse()
	ln, err := net.Listen("tcp", *addr)
	if err != nil {
		log.Fatal(err)
	}
	go func() {
		for range time.Tick(time.Minute) {
			expire(time.Now())
		}
	}()
	log.Printf("listening on %s", *addr)
	serveTCP(ln)
}

func session(id, key string, cl *client, read func() ([]byte, bool)) {
	b, ok := read()
	var hello msg
	if !ok || len(b) > maxSmall || json.Unmarshal(b, &hello) != nil || hello.T != "hello" {
		send(cl, fail("expected hello"))
		return
	}
	rm, seat := join(id, key, cl, hello)
	if rm == nil {
		return
	}
	defer rm.leave(seat, cl)
	for {
		b, ok := read()
		if !ok || !rm.handle(seat, b) {
			return
		}
	}
}

func serveTCP(ln net.Listener) {
	for {
		nc, err := ln.Accept()
		if err != nil {
			return
		}
		go func() {
			defer nc.Close()
			sc := bufio.NewScanner(nc)
			sc.Buffer(make([]byte, 64<<10), maxState+4096)
			read := func() ([]byte, bool) {
				if !sc.Scan() {
					return nil, false
				}
				return append([]byte(nil), sc.Bytes()...), true
			}
			var j struct{ T, Room, Key string }
			b, ok := read()
			if !ok || json.Unmarshal(b, &j) != nil || j.T != "join" || j.Key == "" || len(j.Key) > 64 || j.Room == "" || len(j.Room) > 32 {
				nc.Write([]byte(`{"t":"error","msg":"expected join"}` + "\n"))
				return
			}
			cl := newClient(func() { nc.Close() })
			go func() {
				for b := range cl.out {
					if _, err := nc.Write(append(b, '\n')); err != nil {
						nc.Close()
						return
					}
				}
			}()
			defer close(cl.out)
			session(j.Room, j.Key, cl, read)
			time.Sleep(50 * time.Millisecond)
		}()
	}
}
