import time

if __name__ == "__main__":
    # A background job: no port, and it keeps running until the container stops.
    while True:
        print("Hello from a Python worker", flush=True)
        time.sleep(5)
