import Config

# Railpack's release reads it when it starts; Gitberth gives the app PORT, its web container port.
config :hello, port: String.to_integer(System.get_env("PORT") || "8080")
