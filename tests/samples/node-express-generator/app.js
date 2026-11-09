// The app, as express-generator lays it out: bin/www starts the server that serves it.
var express = require('express');

var app = express();

app.get('/', function (req, res) {
  res.send('Hello from Express, laid out by express-generator');
});

module.exports = app;
