import assert from "node:assert/strict";
import test from "node:test";
import { clearExcludedTimestamps, eraseSeasonTimestamps, eraseTimestamps } from "../src/store/api.ts";

const requests = [];

globalThis.window = {
    ApiClient: {
        serverAddress: () => "http://jellyfin",
        accessToken: () => "token",
    },
};
globalThis.fetch = async (url, init) => {
    requests.push({ url: new URL(url), method: init.method });
    return new Response(null, { status: 204 });
};

// Each query flag is written from its own field. A cache-only erase must keep
// the tombstones; a swapped or dropped flag would erase them.
test("each erase sends the cache and tombstone flags it is given", async () => {
    await eraseTimestamps("Introduction", { eraseCache: true, eraseTombstones: false });
    await eraseSeasonTimestamps("show", "season", { eraseCache: true, eraseTombstones: false });
    await clearExcludedTimestamps({ eraseTombstones: false });

    assert.deepEqual(
        requests.map(({ url, method }) => [
            method,
            url.pathname,
            url.searchParams.get("mode"),
            url.searchParams.get("eraseCache"),
            url.searchParams.get("eraseTombstones"),
        ]),
        [
            ["POST", "/Intros/EraseTimestamps", "Introduction", "true", "false"],
            ["DELETE", "/Intros/Show/show/season", null, "true", "false"],
            ["POST", "/Intros/ExcludedTimestamps/Clear", null, null, "false"],
        ],
    );
});
