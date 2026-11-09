package main

import (
	"html/template"
	"log"
	"net/http"
	"os"
)

func main() {
	// Read from disk at run time, relative to the module root, as many Go apps do.
	page := template.Must(template.ParseFiles("templates/index.html"))
	port := os.Getenv("PORT")
	if port == "" {
		port = "8080"
	}
	http.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		page.Execute(w, "cmd/server")
	})
	log.Fatal(http.ListenAndServe(":"+port, nil))
}
