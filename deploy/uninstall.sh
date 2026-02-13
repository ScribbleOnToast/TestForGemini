#!/bin/bash

echo "--- Starting Digital Eye Uninstallation ---"

echo "Step 1: Removing Systemd Service..."
# 1. Identify the real user (not root)
REAL_USER="${SUDO_USER:-$USER}"
REAL_HOME=$(getent passwd "$REAL_USER" | cut -d: -f6)
REAL_UID=$(id -u "$REAL_USER")

echo "   -> Target User: $REAL_USER"

# 2. Stop and Disable the service (if session is active)
if [ -d "/run/user/$REAL_UID" ]; then
    echo "   -> Found active session, stopping service..."
    sudo -u "$REAL_USER" XDG_RUNTIME_DIR="/run/user/$REAL_UID" systemctl --user stop digitaleye.service || true
    sudo -u "$REAL_USER" XDG_RUNTIME_DIR="/run/user/$REAL_UID" systemctl --user disable digitaleye.service || true
else
    echo "   -> No active session. Skipping stop/disable commands."
fi

# 3. Remove the service file
SERVICE_FILE="$REAL_HOME/.config/systemd/user/digitaleye.service"

if [ -f "$SERVICE_FILE" ]; then
    rm "$SERVICE_FILE"
    echo "   -> Service file removed."
else
    echo "   -> Service file not found, skipping."
fi

# 4. Remove the symlink just in case 'disable' failed or wasn't run
# (This cleans up the 'enabled' state manually)
WANTS_LINK="$REAL_HOME/.config/systemd/user/default.target.wants/digitaleye.service"
if [ -L "$WANTS_LINK" ]; then
    rm "$WANTS_LINK"
    echo "   -> Enabled symlink removed."
fi

# 5. Reload systemd (AS THE USER) so it knows the file is gone
if [ -d "/run/user/$REAL_UID" ]; then
    sudo -u "$REAL_USER" XDG_RUNTIME_DIR="/run/user/$REAL_UID" systemctl --user daemon-reload
fi

# 6. Disable Linger (Optional - undoes the 'start at boot' permission)
loginctl disable-linger "$REAL_USER"

# 2. Remove Application Directory
echo "Step 2: Removing Application Files..."
if [ -d "/opt/digitaleye" ]; then
    sudo rm -rf /opt/digitaleye
    echo "   -> /opt/digitaleye removed."
else
    echo "   -> /opt/digitaleye not found, skipping."
fi

# 3. Uninstall .NET 10
# We installed this to a specific custom directory, so we can safely remove that directory.
echo "Step 3: Removing .NET 10..."
if [ -d "/usr/share/dotnet" ]; then
    sudo rm -rf /usr/share/dotnet
    echo "   -> .NET installation directory removed."
fi

# Remove the global symlink
if [ -L "/usr/bin/dotnet" ]; then
    sudo rm /usr/bin/dotnet
    echo "   -> .NET symlink removed."
fi

# 4. Revert PCIe Gen 3 Setting
# We look for the exact line we added and remove it from config.txt
echo "Step 4: Reverting PCIe Gen 3 Config..."
if grep -q "dtparam=pciex1_gen=3" /boot/firmware/config.txt; then
    # Create a backup just in case
    sudo cp /boot/firmware/config.txt /boot/firmware/config.txt.bak
    # Remove the line
    sudo sed -i '/dtparam=pciex1_gen=3/d' /boot/firmware/config.txt
    echo "   -> PCIe Gen 3 disabled (line removed from config.txt)."
else
    echo "   -> PCIe Gen 3 setting not found, skipping."
fi

# 5. Optional: Remove Installed Dependencies
# We do NOT automatically remove 'python3-pip', 'bluez', etc. as other apps might use them.
# However, we will offer to remove the Hailo-specific packages which are less likely to be shared.
echo "Step 5: Cleaning up specific packages..."
read -p "Do you want to uninstall the Hailo drivers (hailo-h10-all)? [y/N] " -n 1 -r
echo
if [[ $REPLY =~ ^[Yy]$ ]]; then
    sudo apt remove -y hailo-h10-all
    sudo apt autoremove -y
    echo "   -> Hailo drivers removed."
fi

echo "--- Uninstallation Complete! ---"
echo "Note: User group memberships (audio, video, input) and base dependencies (python3, pipewire) were left intact to avoid breaking other applications."