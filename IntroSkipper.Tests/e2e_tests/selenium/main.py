import argparse, os, time

from selenium import webdriver
from selenium.webdriver.common.by import By
from selenium.webdriver.common.keys import Keys


# Driver function
def main():
    # Parse CLI arguments and store in a dictionary
    parser = argparse.ArgumentParser()
    parser.add_argument("-host", help="Jellyfin server address with protocol and port.")
    parser.add_argument("-username", help="Username.")
    parser.add_argument("-password", help="Password.")
    parser.add_argument("-name", help="Name of episode to search for.")
    parser.add_argument(
        "--tests", help="Space separated list of Selenium tests to run.", type=str, nargs="+"
    )
    parser.add_argument(
        "--browsers",
        help="Space separated list of browsers to run tests with.",
        type=str,
        nargs="+",
        choices=["chrome", "firefox"],
    )
    args = parser.parse_args()

    server = {
        "host": args.host,
        "username": args.username,
        "password": args.password,
        "episode": args.name,
        "browsers": args.browsers,
        "tests": args.tests,
    }

    # Print the server info for debugging and run the test
    print()
    print(f"Browsers: {server['browsers']}")
    print(f"Address:  {server['host']}")
    print(f"Username: {server['username']}")
    print(f"Episode:  \"{server['episode']}\"")
    print(f"Tests:    {server['tests']}")
    print()

    # Setup the list of drivers to run tests with
    if server["browsers"] is None:
        print("[!] --browsers is required")
        exit(1)

    drivers = []
    if "chrome" in server["browsers"]:
        drivers = [("http://127.0.0.1:4444", "Chrome")]
    if "firefox" in server["browsers"]:
        drivers.append(("http://127.0.0.1:4445", "Firefox"))

    # Test with all selected drivers
    for driver in drivers:
        print(f"[!] Starting new test run using {driver[1]}")
        test_server(server, driver[0], driver[1])
        print()


# Main server test function
def test_server(server, executor, driver_type):
    # Configure Selenium to use a remote driver
    print(f"[+] Configuring Selenium to use executor {executor} of type {driver_type}")

    opts = None
    if driver_type == "Chrome":
        opts = webdriver.ChromeOptions()
    elif driver_type == "Firefox":
        opts = webdriver.FirefoxOptions()
    else:
        raise ValueError(f"Unknown driver type {driver_type}")

    driver = webdriver.Remote(command_executor=executor, options=opts)

    try:
        # Wait up to two seconds when finding an element before reporting failure
        driver.implicitly_wait(2)

        # Login to Jellyfin
        driver.get(make_url(server, "/"))

        print(f"[+] Authenticating as {server['username']}")
        login(driver, server)

        if "skip_button" in server["tests"]:
            # Play the user specified episode and verify skip intro button functionality. This episode is expected to:
            #   * already have been analyzed for an introduction
            #   * have an introduction at the beginning of the episode
            print("[+] Testing skip intro button")
            test_skip_button(driver, server)

        print("[+] All tests completed successfully")
    finally:
        # Unconditionally end the Selenium session
        driver.quit()


def login(driver, server):
    # Append the Enter key to the password to submit the form
    us = server["username"]
    pw = server["password"] + Keys.ENTER

    # Fill out and submit the login form
    driver.find_element(By.ID, "txtManualName").send_keys(us)
    driver.find_element(By.ID, "txtManualPassword").send_keys(pw)


def test_skip_button(driver, server):
    print(f"  [+] Searching for episode \"{server['episode']}\"")

    search = driver.find_element(By.CSS_SELECTOR, ".headerSearchButton span.search")

    if driver.capabilities["browserName"] == "firefox":
        # Work around a FF bug where the search element isn't considered clickable right away
        time.sleep(1)

    # Click the search button
    search.click()

    # Type the episode name
    driver.find_element(By.CSS_SELECTOR, ".searchfields-txtSearch").send_keys(
        server["episode"]
    )

    # Click the first episode in the search results
    driver.find_element(
        By.CSS_SELECTOR, ".searchResults button[data-type='Episode']"
    ).click()

    # Wait for the episode page to finish loading by searching for the episode description (overview)
    driver.find_element(By.CSS_SELECTOR, ".overview")

    print(f"  [+] Waiting for playback to start")

    # Click the play button in the toolbar
    driver.find_element(
        By.CSS_SELECTOR, "div.mainDetailButtons span.play_arrow"
    ).click()

    # Wait for playback to start by searching for the lower OSD control bar
    driver.find_element(By.CSS_SELECTOR, ".osdControls")

    # Let the video play a little bit so the position before clicking the button can be logged
    print("  [+] Playing video")
    time.sleep(2)
    screenshot(driver, "skip_button_pre_skip")
    assert_video_playing(driver)

    # Find the skip intro button and click it, logging the new video position after the seek is preformed
    print("  [+] Clicking skip intro button")
    driver.find_element(By.CSS_SELECTOR, "div#skipIntro").click()
    time.sleep(1)
    screenshot(driver, "skip_button_post_skip")
    assert_video_playing(driver)

    # Keep playing the video for a few seconds to ensure that:
    #   * the intro was successfully skipped
    #   * video playback continued automatically post button click
    print("  [+] Verifying post skip position")
    time.sleep(4)

    screenshot(driver, "skip_button_post_play")
    assert_video_playing(driver)


# Utility functions
def make_url(server, url):
    final = server["host"] + url
    print(f"[+] Navigating to {final}")
    return final


def screenshot(driver, filename):
    dest = f"screenshots/{filename}.png"
    driver.save_screenshot(dest)


# Returns the current video playback position and if the video is paused.
# Will raise an exception if playback is paused as the video shouldn't ever pause when using this plugin.
def assert_video_playing(driver):
    ret = driver.execute_script(
        """
        const video = document.querySelector("video");
        return {
            "position": video.currentTime,
            "paused": video.paused
        };
        """
    )

    if ret["paused"]:
        raise Exception("Video should not be paused")

    print(f"  [+] Video playback position: {ret['position']}")

    return ret


main()
