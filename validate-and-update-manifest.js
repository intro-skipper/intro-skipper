const https = require('https');
const crypto = require('crypto');
const fs = require('fs');
const { URL } = require('url');

// Read manifest.json
const manifestPath = './manifest.json';
if (!fs.existsSync(manifestPath)) {
    console.error('manifest.json file not found');
    process.exit(1);
}
const jsonData = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));

const newVersion = {
    version: process.env.VERSION, // replace with the actual new version
    changelog: "- See the full changelog at [GitHub](https://github.com/jumoog/intro-skipper/blob/master/CHANGELOG.md)\n",
    targetAbi: "10.9.9.0",
    sourceUrl: process.env.SOURCE_URL,
    checksum: process.env.CHECKSUM,
    timestamp: process.env.TIMESTAMP
};

async function updateManifest() {
    await validVersion(newVersion);

    // Add the new version to the manifest
    jsonData[0].versions.unshift(newVersion);

    // Write the updated manifest to file if validation is successful
    fs.writeFileSync(manifestPath, JSON.stringify(jsonData, null, 4));
    console.log('Manifest updated successfully.');
    process.exit(0); // Exit with no error
}

async function validVersion(version) {
    console.log(`Validating version ${version.version}...`);

    const isValidUrl = await checkUrl(version.sourceUrl);
    if (!isValidUrl) {
        console.error(`Invalid URL: ${version.sourceUrl}`);
        process.exit(1); // Exit with an error code
    }

    const isValidChecksum = await verifyChecksum(version.sourceUrl, version.checksum);
    if (!isValidChecksum) {
        console.error(`Checksum mismatch for URL: ${version.sourceUrl}`);
        process.exit(1); // Exit with an error code
    } else {
        console.log(`Version ${version.version} is valid.`);
    }
}

function checkUrl(url) {
    return new Promise((resolve) => {
        https.get(url, (res) => {
            resolve(res.statusCode === 302);
        }).on('error', () => {
            resolve(false);
        });
    });
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

async function run() {
    await updateManifest();
}

run();
