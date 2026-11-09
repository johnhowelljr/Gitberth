class HelloController < ActionController::Base
  def index
    render plain: "Hello from Rails"
  end
end
