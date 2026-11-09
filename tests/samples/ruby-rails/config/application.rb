require_relative "boot"

# Only Action Controller: no database, mailer, jobs or asset pipeline.
require "rails"
require "action_controller/railtie"

Bundler.require(*Rails.groups)

module Hello
  class Application < Rails::Application
    config.load_defaults 8.1
  end
end
