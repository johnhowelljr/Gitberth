Rails.application.configure do
  config.enable_reloading = false
  config.eager_load = true
  config.consider_all_requests_local = false
  config.logger = ActiveSupport::TaggedLogging.logger(STDOUT)
end
