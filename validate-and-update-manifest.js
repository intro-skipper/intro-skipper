const https = require('https');
const crypto = require('crypto');
const fs = require('fs');
const { URL } = require('url');

const repository = process.env.GITHUB_REPOSITORY;
const version = process.env.VERSION;
let currentVersion = "";
let targetAbi = "";

// Read manifest.json
const manifestPath = './manifest.json';
if (!fs.existsSync(manifestPath)) {
    console.error('manifest.json file not found');
    process.exit(1);
}

// Read README.md
const readmePath = './README.md';
if (!fs.existsSync(readmePath)) {
    console.error('README.md file not found');
    process.exit(1);
}

// Read .github/ISSUE_TEMPLATE/bug_report_form.yml
const bugReportFormPath = './.github/ISSUE_TEMPLATE/bug_report_form.yml';
if (!fs.existsSync(bugReportFormPath)) {
    console.error(`${bugReportFormPath} file not found`);
    process.exit(1);
}

const jsonData = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));

async function updateManifest() {
    currentVersion = await getNugetPackageVersion('Jellyfin.Model', '10.*-*');
    targetAbi = `${currentVersion}.0`
    const newVersion = {
        version,
        changelog: `- See the full changelog at [GitHub](https://github.com/${repository}/releases/tag/10.9/v${version})\n`,
        targetAbi,
        sourceUrl: `https://github.com/${repository}/releases/download/10.9/v${version}/intro-skipper-v${version}.zip`,
        checksum: getMD5FromFile(),
        timestamp: new Date().toISOString().replace(/\.\d{3}Z$/, 'Z')
    };
    await validVersion(newVersion);

    // Add the new version to the manifest
    jsonData[0].versions.unshift(newVersion);

    console.log('Manifest updated successfully.');
    updateDocsVersion(readmePath);
    updateDocsVersion(bugReportFormPath);

    cleanUpOldReleases();

    // Write the modified JSON data back to the file
    fs.writeFileSync(manifestPath, JSON.stringify(jsonData, null, 4), 'utf8');

    process.exit(0); // Exit with no error
}

async function validVersion(version) {
    console.log(`Validating version ${version.version}...`);

    const isValidChecksum = await verifyChecksum(version.sourceUrl, version.checksum);
    if (!isValidChecksum) {
        console.error(`Checksum mismatch for URL: ${version.sourceUrl}`);
        process.exit(1); // Exit with an error code
    } else {
        console.log(`Version ${version.version} is valid.`);
    }
}

async function verifyChecksum(url, expectedChecksum) {
    try {
        const hash = await downloadAndHashFile(url);
        return hash === expectedChecksum;
    } catch (error) {
        console.error(`Error verifying checksum for URL: ${url}`, error);
        return false;
    }
}

async function downloadAndHashFile(url, redirects = 5) {
    if (redirects === 0) {
        throw new Error('Too many redirects');
    }

    return new Promise((resolve, reject) => {
        https.get(url, (response) => {
            if (response.statusCode >= 300 && response.statusCode < 400 && response.headers.location) {
                // Follow redirect
                const redirectUrl = new URL(response.headers.location, url).toString();
                downloadAndHashFile(redirectUrl, redirects - 1)
                    .then(resolve)
                    .catch(reject);
            } else if (response.statusCode === 200) {
                const hash = crypto.createHash('md5');
                response.pipe(hash);
                response.on('end', () => {
                    resolve(hash.digest('hex'));
                });
                response.on('error', (err) => {
                    reject(err);
                });
            } else {
                reject(new Error(`Failed to get '${url}' (${response.statusCode})`));
            }
        }).on('error', (err) => {
            reject(err);
        });
    });
}

function getMD5FromFile() {
    const fileBuffer = fs.readFileSync(`intro-skipper-v${version}.zip`);
    return crypto.createHash('md5').update(fileBuffer).digest('hex');
}

function updateDocsVersion(docsPath) {
    const readMeContent = fs.readFileSync(docsPath, 'utf8');

    const updatedContent = readMeContent
        .replace(/Jellyfin.*\(or newer\)/, `Jellyfin ${currentVersion} (or newer)`)
    if (readMeContent != updatedContent) {
        fs.writeFileSync(docsPath, updatedContent);
        console.log(`Updated ${docsPath} with new Jellyfin version.`);
    } else {
        console.log(`${docsPath} has already newest Jellyfin version.`);
    }
}

function cleanUpOldReleases() {
    // Extract all unique targetAbi values
    const abiSet = new Set();
    jsonData.forEach(entry => {
        entry.versions.forEach(version => {
            abiSet.add(version.targetAbi);
        });
    });

    // Convert the Set to an array and sort it in descending order
    const abiArray = Array.from(abiSet).sort((a, b) => {
        const aParts = a.split('.').map(Number);
        const bParts = b.split('.').map(Number);

        for (let i = 0; i < aParts.length; i++) {
            if (aParts[i] > bParts[i]) return -1;
            if (aParts[i] < bParts[i]) return 1;
        }
        return 0;
    });

    // Identify the highest and second highest targetAbi
    const highestAbi = abiArray[0];
    const secondHighestAbi = abiArray[1];

    // Filter the versions array to keep only those with the highest or second highest targetAbi
    jsonData.forEach(entry => {
        entry.versions = entry.versions.filter(version =>
            version.targetAbi === highestAbi || version.targetAbi === secondHighestAbi
        );
    });
}

async function getNugetPackageVersion(packageName, versionPattern) {
    // Convert package name to lowercase for the NuGet API
    const url = `https://api.nuget.org/v3-flatcontainer/${packageName.toLowerCase()}/index.json`;

    try {
        // Fetch data using the built-in fetch API
        const response = await fetch(url);

        if (!response.ok) {
            throw new Error(`Failed to fetch package information: ${response.statusText}`);
        }

        const data = await response.json();
        const versions = data.versions;

        // Create a regular expression from the version pattern
        const versionRegex = new RegExp(versionPattern.replace(/\./g, '\\.').replace('*', '.*'));

        // Filter versions based on the provided pattern
        const matchingVersions = versions.filter(version => versionRegex.test(version));

        // Check if there are any matching versions
        if (matchingVersions.length > 0) {
            const latestVersion = matchingVersions[matchingVersions.length - 1];
            console.log(`Latest version of ${packageName} matching ${versionPattern}: ${latestVersion}`);
            return latestVersion;
        } else {
            console.log(`No versions of ${packageName} match the pattern ${versionPattern}`);
            return null;
        }
    } catch (error) {
        console.error(`Error fetching package information for ${packageName}: ${error.message}`);
    }
}

async function run() {
    await updateManifest();
}

run();
