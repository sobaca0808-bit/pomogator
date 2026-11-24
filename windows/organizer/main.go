// The organizer runs locally in the default browser.
package main

import (
	"embed"
	"fmt"
	"io/fs"
	"net"
	"net/http"
	"os"
	"os/exec"
	"time"
)

//go:embed all:dist
var assets embed.FS

const address = "127.0.0.1:47821"

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		fmt.Println("Нажмите Enter для выхода.")
		fmt.Scanln()
		os.Exit(1)
	}
}

func run() error {
	deployed, err := fs.Sub(assets, "dist")
	if err != nil {
		return err
	}
	listener, err := net.Listen("tcp4", address)
	if err != nil {
		return fmt.Errorf("Не удалось открыть локальный адрес %s. Если ПОМОГАТОР уже запущен, откройте его вкладку.", address)
	}
	defer listener.Close()
	url := "http://" + address + "/"
	server := &http.Server{
		ReadHeaderTimeout: 5 * time.Second,
		IdleTimeout:       30 * time.Second,
		Handler: http.HandlerFunc(func(w http.ResponseWriter, request *http.Request) {
			if request.Host != address {
				http.Error(w, "Invalid host", http.StatusForbidden)
				return
			}
			w.Header().Set("Cache-Control", "no-cache")
			http.FileServer(http.FS(deployed)).ServeHTTP(w, request)
		}),
	}
	fmt.Println("ПОМОГАТОР открыт по адресу " + url)
	fmt.Println("Чтобы остановить приложение, закройте это окно.")
	if len(os.Args) != 2 || os.Args[1] != "--no-browser" {
		if err := exec.Command("rundll32.exe", "url.dll,FileProtocolHandler", url).Start(); err != nil {
			fmt.Println("Откройте адрес вручную в браузере.")
		}
	}
	return server.Serve(listener)
}
