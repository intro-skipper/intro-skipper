# Installing the Modified Jellyfin Web Interface

## Requirements

- **Jellyfin Version**: 10.9
- **Modified Web Interface**: Download the latest version from [GitHub Actions](https://github.com/intro-skipper/intro-skipper/actions/workflows/webui.yml)
  1. Open the most recent action run.
  2. In the "Artifacts" section, click the `jellyfin-web-VERSION+COMMIT.zip` link to download the pre-compiled web interface. *Note: You must be signed into GitHub to access this link.*

## Native Installation (Linux/Windows)

1. **Backup the Original Web Interface**:
   - On **Linux**: The web interface is located at `/usr/share/jellyfin/web/`.
   - On **Windows**: The web interface is located at `C:\Program Files\Jellyfin\Server\jellyfin-web`.
  
2. **Install the Modified Web Interface**:
   - Extract the contents of the downloaded zip file.
   - Copy the extracted files into Jellyfin's web directory, replacing the existing files.

3. **Plugin Installation**:
   - Follow the plugin installation instructions provided in the main README.

## Container Installation

1. **Extract the Archive**:
   - Extract the downloaded archive on your server.
   - Note the full path to the `dist` folder.

2. **Update Docker Compose**:
   - Mount the `dist` folder in your container using the appropriate path:
     ```yaml
     services:
       jellyfin:
         ports:
           - "8096:8096"
         volumes:
           - "/full/path/to/extracted/dist:/jellyfin/jellyfin-web:ro"  # For the official container
           - "/full/path/to/extracted/dist:/usr/share/jellyfin/web:ro" # For the linuxserver container
           - "/config:/config"
           - "/media:/media:ro"
         image: "jellyfin/jellyfin:latest"
     ```

3. **Clear Browser Cache**:
   - Ensure you clear your browser's cache before testing the new web interface.

### Unraid Users

For Unraid users, follow these additional steps:

1. In the **Docker** tab, click on the Jellyfin container.
2. Click on **Edit** and enable **Advanced View**.
3. Under **Extra Parameters**, add the appropriate volume mount command:
   - For the `jellyfin/jellyfin` container: `--volume /full/path/to/extracted/dist:/jellyfin/jellyfin-web:ro`
   - For the `linuxserver/jellyfin` container: `--volume /full/path/to/extracted/dist:/usr/share/jellyfin/web:ro`

### Note for Jellyfin Media Player Users

If you are using **Jellyfin Media Player (JMP)**, make sure that the "Intro Skipper Plugin" option is disabled in the JMP settings. This ensures compatibility with the modified web interface and avoids potential conflicts with the intro-skipping functionality.
