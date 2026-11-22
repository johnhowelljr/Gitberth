defmodule Hello do
  @moduledoc "Answers on the port config/runtime.exs reads from PORT."
  use Application

  @impl true
  def start(_type, _args) do
    port = Application.fetch_env!(:hello, :port)
    children = [{Plug.Cowboy, scheme: :http, plug: Hello.Router, options: [port: port]}]
    Supervisor.start_link(children, strategy: :one_for_one, name: Hello.Supervisor)
  end
end

defmodule Hello.Router do
  use Plug.Router

  plug :match
  plug :dispatch

  get "/" do
    send_resp(conn, 200, "Hello from Elixir")
  end

  match _ do
    send_resp(conn, 404, "Not found")
  end
end
