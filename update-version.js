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

updateCsprojVersion()