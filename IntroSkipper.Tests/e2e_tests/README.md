# End to end testing framework

## wrapper

The wrapper script (compiled as `run_tests`) runs multiple tests on Jellyfin servers to verify that the plugin works as intended. It tests:

- Introduction timestamp accuracy (using `verifier`)
- Web interface functionality (using `selenium/main.py`)

## verifier

### Description

This program is responsible for:
* Saving all discovered introduction timestamps into a report
* Comparing two reports against each other to find episodes that:
    * Are missing introductions in both reports
    * Have introductions in both reports, but with different timestamps
    * Newly discovered introductions
    * Introductions that were discovered previously, but not anymore
* Validating the schema of returned `Intro` objects from the `/IntroTimestamps` API endpoint

### Usage examples
* Generate intro timestamp report from a local server:
    * `./verifier -address http://127.0.0.1:8096 -key api_key`
* Generate intro timestamp report from a remote server, polling for task completion every 20 seconds:
    * `./verifier -address https://example.com -key api_key -poll 20s -o example.json`
* Compare two previously generated reports:
    * `./verifier -r1 v0.1.5.json -r2 v0.1.6.json`
* Validate the API schema for three episodes:
    * `./verifier -address http://127.0.0.1:8096 -key api_key -validate id1,id2,id3`

## Selenium web interface tests

Selenium is used to verify that the plugin's web interface works as expected. It simulates a user:

* Clicking the skip intro button
    * Checks that clicking the button skips the intro and keeps playing the video
* Changing settings (will be added in the future)
    * Maximum degree of parallelism
    * Selecting libraries for analysis
    * EDL settings
    * Introduction requirements
    * Auto skip
    * Show/hide skip prompt
* Timestamp editor (will be added in the future)
    * Displays timestamps
    * Modifies timestamps
    * Erases season timestamps
* Fingerprint visualizer (will be added in the future)
    * Suggests shifts
    * Visualizer canvas is drawn on
