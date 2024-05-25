const fs = require('fs');

// Read csproj
const csprojPath = './ConfusedPolarBear.Plugin.IntroSkipper/ConfusedPolarBear.Plugin.IntroSkipper.csproj';
if (!fs.existsSync(csprojPath)) {
    console.error('ConfusedPolarBear.Plugin.IntroSkipper.csproj file not found');
    process.exit(1);
}

function updateCsprojVersion() {
    const newVersion = process.env.VERSION
    const csprojContent = fs.readFileSync(csprojPath, 'utf8');

    const updatedContent = csprojContent
        .replace(/<AssemblyVersion>.*<\/AssemblyVersion>/, `<AssemblyVersion>${newVersion}</AssemblyVersion>`)
        .replace(/<FileVersion>.*<\/FileVersion>/, `<FileVersion>${newVersion}</FileVersion>`);

    fs.writeFileSync(csprojPath, updatedContent);
    console.log('Updated .csproj file with new version.');
}

// Function to increment version string
function incrementVersion(version) {
    const parts = version.split('.').map(Number);
    parts[parts.length - 1] += 1; // Increment the last part of the version
    return parts.join('.');
}

// Read the .csproj file
fs.readFile(csprojPath, 'utf8', (err, data) => {
    if (err) {
        return console.error('Failed to read .csproj file:', err);
    }

    let newAssemblyVersion = null;
    let newFileVersion = null;

    // Use regex to find and increment versions
    const updatedData = data.replace(/<AssemblyVersion>(.*?)<\/AssemblyVersion>/, (match, version) => {
        newAssemblyVersion = incrementVersion(version);
        return `<AssemblyVersion>${newAssemblyVersion}</AssemblyVersion>`;
    }).replace(/<FileVersion>(.*?)<\/FileVersion>/, (match, version) => {
        newFileVersion = incrementVersion(version);
        return `<FileVersion>${newFileVersion}</FileVersion>`;
    });

    // Write the updated XML back to the .csproj file
    fs.writeFile(csprojPath, updatedData, 'utf8', (err) => {
        if (err) {
            return console.error('Failed to write .csproj file:', err);
        }
        console.log('Version incremented successfully!');

        // Write the new versions to GitHub Actions environment files
        fs.appendFileSync(process.env.GITHUB_ENV, `NEW_ASSEMBLY_VERSION=${newAssemblyVersion}\n`);
        fs.appendFileSync(process.env.GITHUB_ENV, `NEW_FILE_VERSION=${newFileVersion}\n`);
    });
});
