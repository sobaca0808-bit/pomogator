// Synthetic TLS 1.3 cover for the REALITY integration test.
package main

import (
	"crypto/tls"
	"io"
	"log"
	"net"
	"os"
	"time"
)

func main() {
	certificate, err := tls.LoadX509KeyPair(os.Args[2], os.Args[3])
	if err != nil {
		log.Fatal("cannot load fixture certificate")
	}
	listener, err := tls.Listen("tcp4", os.Args[1], &tls.Config{
		MinVersion:       tls.VersionTLS13,
		Certificates:     []tls.Certificate{certificate},
		NextProtos:       []string{"h2", "http/1.1"},
		CurvePreferences: []tls.CurveID{tls.X25519MLKEM768, tls.X25519},
	})
	if err != nil {
		log.Fatal("cannot open the synthetic TLS listener")
	}
	defer listener.Close()
	for {
		conn, err := listener.Accept()
		if err != nil {
			log.Fatal("cannot accept a cover connection")
		}
		go drain(conn)
	}
}

func drain(conn net.Conn) {
	defer conn.Close()
	conn.SetDeadline(time.Now().Add(10 * time.Second))
	io.Copy(io.Discard, conn)
}
