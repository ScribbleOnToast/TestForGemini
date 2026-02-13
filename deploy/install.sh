#!/bin/bash
SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
# Exit on error
set -e

echo "--- Starting Digital Eye Deployment ---"

# 1. Update and Install Base Dependencies
sudo apt update && sudo apt upgrade -y

sudo apt install -y python3-pip python3-venv libcamera-apps-lite dkms \
    pipewire pipewire-pulse pipewire-alsa pulseaudio-utils wireplumber \
    libspa-0.2-bluetooth libportaudio2 bluez hailo-h10-all \
    libgirepository-2.0-dev libcairo2-dev pkg-config python3-picamera2 \
    pipewire-audio-client-libraries

# 2. Add user to necessary groups for audio, video, and input device access
sudo usermod -aG audio,bluetooth,video,input $USER

# 3. Enable PCIe Gen 3 for Hailo-10H
# The Pi 5 defaults to Gen 2; we want the full pipe for the Hailo10h.
if ! grep -q "dtparam=pciex1_gen=3" /boot/firmware/config.txt; then
    echo "Enabling PCIe Gen 3..."
    echo "dtparam=pciex1_gen=3" | sudo tee -a /boot/firmware/config.txt
fi

# 4. .NET 10 Installation
# Using --install-dir to ensure we know exactly where it lives for the script
echo "Installing .NET 10..."
sudo curl -sSL https://dot.net/v1/dotnet-install.sh | sudo bash /dev/stdin --channel 10.0 --install-dir /usr/share/dotnet
# Create a symlink so 'dotnet' is available globally immediately
sudo ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet

# 5. Setup digitaleye Directory Structure
sudo mkdir -p /opt/digitaleye
sudo chown $USER:$USER /opt/digitaleye
sudo rsync -avP -P $SCRIPT_DIR/apps/ /opt/digitaleye/
sudo cp $SCRIPT_DIR/configs/requirements.txt /opt/digitaleye/
sudo chmod +x /opt/digitaleye/DigitalEye

# 6. Python Environment & Hailo Pip Installs
echo "cd "
cd /opt/digitaleye
echo "go env "
sudo python3 -m venv --system-site-packages venv 
echo "act "
source venv/bin/activate
echo "pip inst 1 "
pip install --upgrade pip setuptools wheel
if [ -f "requirements.txt" ]; then
    echo "pip2 "
    pip install -r requirements.txt
fi

# 7. Configure the Pi Camera 3 Wide
# Ensuring the camera stack is ready for libcamera/OpenCV
sudo raspi-config nonint do_camera 0 
sudo raspi-config nonint do_boot_behaviour B2
# 8. Install the Systemd Service
echo "--- Configuring Systemd Service ---"
    # 1. Identify the real user (not root)
    REAL_USER="${SUDO_USER:-$USER}"
    REAL_HOME=$(getent passwd "$REAL_USER" | cut -d: -f6)
    REAL_UID=$(id -u "$REAL_USER")

    echo "   -> Target User: $REAL_USER"
    echo "   -> Target Home: $REAL_HOME"

    # 2. Create the systemd user directory as the real user
    sudo -u "$REAL_USER" mkdir -p "$REAL_HOME/.config/systemd/user/"

    # 3. Copy the service file and fix ownership
    # Note: We assume 'digitaleye.service' is in 'configs/systemd' relative to the script
    cp "$SCRIPT_DIR/configs/systemd/digitaleye.service" "$REAL_HOME/.config/systemd/user/"
    chown "$REAL_USER:$REAL_USER" "$REAL_HOME/.config/systemd/user/digitaleye.service"

    # 4. Enable "Linger" 
    # This ensures the user service starts at boot even if the user isn't logged in
    loginctl enable-linger "$REAL_USER"

    # 5. Reload and Enable (The tricky part)
    # We must switch context to the user AND point to their runtime directory
    if [ -d "/run/user/$REAL_UID" ]; then
        echo "   -> Found active session, reloading systemd..."
        sudo -u "$REAL_USER" XDG_RUNTIME_DIR="/run/user/$REAL_UID" systemctl --user daemon-reload
        sudo -u "$REAL_USER" XDG_RUNTIME_DIR="/run/user/$REAL_UID" systemctl --user enable digitaleye.service
        echo "   -> Service enabled!"
    else
        echo "   -> No active session found. Creating symlink manually..."
        # Fallback: Manually enable if the user isn't currently logged in
        sudo -u "$REAL_USER" mkdir -p "$REAL_HOME/.config/systemd/user/default.target.wants"
        sudo -u "$REAL_USER" ln -sf "$REAL_HOME/.config/systemd/user/digitaleye.service" "$REAL_HOME/.config/systemd/user/default.target.wants/digitaleye.service"
        echo "   -> Service symlinked manually."
    fi

echo "--- Deployment Complete! Rebooting in 5 seconds ---"
sleep 5
sudo reboot