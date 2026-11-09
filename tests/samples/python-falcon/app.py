import falcon


class Hello:
    def on_get(self, req, resp):
        resp.content_type = falcon.MEDIA_TEXT
        resp.text = "Hello from Falcon"


app = falcon.App()
app.add_route("/", Hello())
