const https = require('https');
const crypto = require('crypto');
const fs = require('fs');
const { URL } = require('url');

const repository = process.env.GITHUB_REPOSITORY;
const version = process.env.VERSION;
const targetAbi = "10.9.9.0";

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

const jsonData = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));

const newVersion = {
    version,
    changelog: `- See the full changelog at [GitHub](https://github.com/${repository}/releases/tag/10.9/v${version})\n`,
    targetAbi,
    sourceUrl: `https://github.com/${repository}/releases/download/10.9/v${version}/intro-skipper-v${version}.zip`,
    checksum: getMD5FromFile(),
    timestamp: new Date().toISOString().replace(/\.\d{3}Z$/, 'Z')
};

async function updateManifest() {
    await validVersion(newVersion);

    // Add the new version to the manifest
    jsonData[0].versions.unshift(newVersion);

    // Write the updated manifest to file if validation is successful
    fs.writeFileSync(manifestPath, JSON.stringify(jsonData, null, 4));
    console.log('Manifest updated successfully.');
    updateReadMeVersion();
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

function getReadMeVersion() {
    let parts = targetAbi.split('.').map(Number);
    parts.pop();
    return parts.join(".");
}

function updateReadMeVersion() {
    const newVersion = getReadMeVersion();
    const readMeContent = fs.readFileSync(readmePath, 'utf8');

    const updatedContent = readMeContent
        .replace(/Jellyfin.*\(or newer\)/, `Jellyfin ${newVersion} (or newer)`)
    if (readMeContent != updatedContent) {
        fs.writeFileSync(readmePath, updatedContent);
        console.log('Updated README with new Jellyfin version.');
    } else {
        console.log('README has already newest Jellyfin version.');
    }
}

async function run() {
    await updateManifest();
}

run();
