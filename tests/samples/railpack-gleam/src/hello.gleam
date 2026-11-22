import envoy
import gleam/bytes_tree
import gleam/erlang/process
import gleam/http/response
import gleam/int
import gleam/result
import mist

// Answers every request on PORT, the web container port Gitberth gives it.
pub fn main() {
  let port = envoy.get("PORT") |> result.try(int.parse) |> result.unwrap(8080)
  let assert Ok(_) =
    fn(_request) {
      response.new(200)
      |> response.set_body(mist.Bytes(bytes_tree.from_string("Hello from Gleam")))
    }
    |> mist.new
    |> mist.bind("0.0.0.0")
    |> mist.port(port)
    |> mist.start
  process.sleep_forever()
}
