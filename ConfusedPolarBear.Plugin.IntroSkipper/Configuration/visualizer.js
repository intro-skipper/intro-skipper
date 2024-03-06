// re-render the troubleshooter with the latest offset
function renderTroubleshooter() {
    paintFingerprintDiff(canvas, lhs, rhs, Number(offset.value));
    findIntros();
}

// refresh the upper & lower bounds for the offset
function refreshBounds() {
    const len = Math.min(lhs.length, rhs.length) - 1;
    offset.min = -1 * len;
    offset.max = len;
}

function findIntros() {
    let times = [];

    // get the times of all similar fingerprint points
    for (let i in fprDiffs) {
        if (fprDiffs[i] > fprDiffMinimum) {
            times.push(i * 0.128);
        }
    }

    // always close the last range
    times.push(Number.MAX_VALUE);

    let last = times[0];
    let start = last;
    let end = last;
    let ranges = [];

    for (let t of times) {
        const diff = t - last;

        if (diff <= 3.5) {
            end = t;
            last = t;
            continue;
        }

        const dur = Math.round(end - start);
        if (dur >= 15) {
            ranges.push({
                "start": start,
                "end": end,
                "duration": dur
            });
        }

        start = t;
        end = t;
        last = t;
    }

    const introsLog = document.querySelector("span#intros");
    introsLog.style.position = "relative";
    introsLog.style.left = "115px";
    introsLog.innerHTML = "";

    const offset = Number(txtOffset.value) * 0.128;
    for (let r of ranges) {
        let lStart, lEnd, rStart, rEnd;

        if (offset < 0) {
            // negative offset, the diff is aligned with the RHS
            lStart = r.start - offset;
            lEnd = r.end - offset;
            rStart = r.start;
            rEnd = r.end;

        } else {
            // positive offset, the diff is aligned with the LHS
            lStart = r.start;
            lEnd = r.end;
            rStart = r.start + offset;
            rEnd = r.end + offset;
        }

        const lTitle = selectEpisode1.options[selectEpisode1.selectedIndex].text;
        const rTitle = selectEpisode2.options[selectEpisode2.selectedIndex].text;
        introsLog.innerHTML += "<span>" + lTitle + ": " +
            secondsToString(lStart) + " - " + secondsToString(lEnd) + "</span> <br />";
        introsLog.innerHTML += "<span>" + rTitle + ": " +
            secondsToString(rStart) + " - " + secondsToString(rEnd) + "</span> <br />";
    }
}

// find all shifts which align exact matches of audio.
function findExactMatches() {
    let shifts = [];

    for (let lhsIndex in lhs) {
        let lhsPoint = lhs[lhsIndex];
        let rhsIndex = rhs.findIndex((x) => x === lhsPoint);

        if (rhsIndex === -1) {
            continue;
        }

        let shift = rhsIndex - lhsIndex;
        if (shifts.includes(shift)) {
            continue;
        }

        shifts.push(shift);
    }

    // Only suggest up to 20 shifts
    shifts = shifts.slice(0, 20);

    txtSuggested.textContent = "Suggested shifts: ";
    if (shifts.length === 0) {
        txtSuggested.textContent += "none available";
    } else {
        shifts.sort((a, b) => { return a - b });
        txtSuggested.textContent += shifts.join(", ");
    }
}

// The below two functions were modified from https://github.com/dnknth/acoustid-match/blob/ffbf21d8c53c40d3b3b4c92238c35846545d3cd7/fingerprints/static/fingerprints/fputils.js
// Originally licensed as MIT.
function renderFingerprintData(ctx, fp, xor = false) {
    const pixels = ctx.createImageData(32, fp.length);
    let idx = 0;

    for (let i = 0; i < fp.length; i++) {
        for (let j = 0; j < 32; j++) {
            if (fp[i] & (1 << j)) {
                pixels.data[idx + 0] = 255;
                pixels.data[idx + 1] = 255;
                pixels.data[idx + 2] = 255;

            } else {
                pixels.data[idx + 0] = 0;
                pixels.data[idx + 1] = 0;
                pixels.data[idx + 2] = 0;
            }

            pixels.data[idx + 3] = 255;
            idx += 4;
        }
    }

    if (!xor) {
        return pixels;
    }

    // if rendering the XOR of the fingerprints, count how many bits are different at each timecode
    fprDiffs = [];

    for (let i = 0; i < fp.length; i++) {
        let count = 0;

        for (let j = 0; j < 32; j++) {
            if (fp[i] & (1 << j)) {
                count++;
            }
        }

        // push the percentage similarity
        fprDiffs[i] = 100 - (count * 100) / 32;
    }

    return pixels;
}

function paintFingerprintDiff(canvas, fp1, fp2, offset) {
    if (fp1.length == 0) {
        return;
    }

    canvas.style.display = "unset";

    let leftOffset = 0, rightOffset = 0;
    if (offset < 0) {
        leftOffset -= offset;
    } else {
        rightOffset += offset;
    }

    let fpDiff = [];
    fpDiff.length = Math.min(fp1.length, fp2.length) - Math.abs(offset);
    for (let i = 0; i < fpDiff.length; i++) {
        fpDiff[i] = fp1[i + leftOffset] ^ fp2[i + rightOffset];
    }

    const ctx = canvas.getContext('2d');
    const pixels1 = renderFingerprintData(ctx, fp1);
    const pixels2 = renderFingerprintData(ctx, fp2);
    const pixelsDiff = renderFingerprintData(ctx, fpDiff, true);
    const border = 4;

    canvas.width = pixels1.width + border + // left fingerprint
        pixels2.width + border +            // right fingerprint
        pixelsDiff.width + border           // fingerprint diff
        + 4;                                // if diff[x] >= fprDiffMinimum

    canvas.height = Math.max(pixels1.height, pixels2.height) + Math.abs(offset);

    ctx.rect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = "#C5C5C5";
    ctx.fill();

    // draw left fingerprint
    let dx = 0;
    ctx.putImageData(pixels1, dx, rightOffset);
    dx += pixels1.width + border;

    // draw right fingerprint
    ctx.putImageData(pixels2, dx, leftOffset);
    dx += pixels2.width + border;

    // draw fingerprint diff
    ctx.putImageData(pixelsDiff, dx, Math.abs(offset));
    dx += pixelsDiff.width + border;

    // draw the fingerprint diff similarity indicator
    // https://davidmathlogic.com/colorblind/#%23EA3535-%232C92EF
    for (let i in fprDiffs) {
        const j = Number(i);
        const y = Math.abs(offset) + j;
        const point = fprDiffs[j];

        if (point >= 100) {
            ctx.fillStyle = "#002FFF"
        } else if (point >= fprDiffMinimum) {
            ctx.fillStyle = "#2C92EF";
        } else {
            ctx.fillStyle = "#EA3535";
        }

        ctx.fillRect(dx, y, 4, 1);
    }
}
