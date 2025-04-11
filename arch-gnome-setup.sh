#!/bin/bash

# Install GNOME and essential packages
echo "Installing GNOME and essential packages..."
sudo pacman -S gnome-session gnome-shell gdm gnome-console gnome-software gnome-backgrounds \
    gnome-themes-extra gnome-tweaks gnome-calculator gnome-control-center gnome-settings-daemon \
    gnome-text-editor gnome-screenshot gnome-shell-extension-desktop-icons-ng gnome-disk-utility \
    gvfs gvfs-smb gvfs-nfs gvfs-mtp gvfs-afc gvfs-dnssd gvfs-wsdd eog nautilus nautilus-share \
    ttf-ubuntu-font-family discord goverlay telegram-desktop gamemode flatpak obs-studio mumble \
    steam noto-fonts-emoji

# Enable GDM service
echo "Enabling GDM service..."
sudo systemctl enable gdm

# Create empty text file template
echo "Creating template file..."
mkdir -p ~/Templates
touch ~/Templates/txt

# Install base development packages
echo "Installing base development packages..."
sudo pacman -S --needed base-devel git

# Install YAY AUR helper
echo "Installing YAY AUR helper..."
git clone https://aur.archlinux.org/yay.git
cd yay
makepkg -si
cd ..
sudo rm -r yay

# Install Yaru themes from AUR
echo "Installing Yaru themes..."
yay -S yaru-gnome-shell-theme yaru-gtk-theme yaru-icon-theme yaru-sound-theme

# Install Flatpak applications
echo "Installing Flatpak applications..."
flatpak install flathub com.mattjakeman.ExtensionManager org.nickvision.tubeconverter com.github.wwmm.easyeffects

# Install NVIDIA drivers and CUDA
echo "Installing NVIDIA drivers and CUDA..."
sudo pacman -S linux-zen-headers nvidia-dkms nvidia-settings nvidia-utils cuda cuda-tools libglvnd lib32-nvidia-utils lib32-libglvnd

echo "Installation complete!"
