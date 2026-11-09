package main

import (
	"log/slog"
	"net/http"

	"github.com/labstack/echo/v5"
)

func main() {
	e := echo.New()
	e.GET("/", func(c *echo.Context) error {
		return c.String(http.StatusOK, "Hello from Echo")
	})
	if err := e.Start(":1323"); err != nil {
		slog.Error("failed to start server", "error", err)
	}
}
