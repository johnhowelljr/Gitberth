package main

import "github.com/gin-gonic/gin"

func main() {
	r := gin.Default()
	r.GET("/", func(c *gin.Context) {
		c.String(200, "Hello from Gin")
	})
	// With no address, Gin listens on PORT, or 8080.
	r.Run()
}
