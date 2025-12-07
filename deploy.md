# Deployment Guide

## Core Recommendation: Mac Mini Host + VPS Gateway
Given your hardware (Mac Mini M4, Always-on, 4W power) and network (Tailscale), the **Hybrid Topology** is the best choice.

**Topology:**
`User` -> `Internet` -> **VPS (Nginx Gateway)** -> `[Tailscale]` -> **Mac Mini (Docker App)** -> `[LAN]` -> **NAS**

**Why this is best:**
1.  **Scanning Speed**: Mac Mini accesses NAS via Gigabit LAN. Scanning 1TB takes minutes, not hours. (Tailscale would bottleneck this).
2.  **Transcoding Power**: M4 chip is significantly more powerful than most VPS vCPUs for converting audio.
3.  **Efficiency**: Only the *compressed* audio stream (e.g., 128kbps/320kbps) goes over the Tailscale tunnel to the VPS, saving bandwidth.
4.  **Security**: Your home IP is hidden. Users connect to the VPS Public IP.

---

## Part A: Mac Mini Setup (The App)
Run the application on your Mac Mini.

1.  **Prepare Directory**:
    ```bash
    mkdir -p ~/webmusic_deploy
    cp docker-compose.yml ~/webmusic_deploy/
    cp -r backend ~/webmusic_deploy/
    cp -r frontend ~/webmusic_deploy/
    cd ~/webmusic_deploy
    ```

2.  **Start Docker**:
    ```bash
    docker-compose up -d --build
    ```

3.  **Verify Local Access**:
    Open `http://localhost:5173` on the Mac. It should work.

4.  **Identify Tailscale IP**:
    Run `tailscale ip -4` on the Mac.
    *Example: `100.100.10.10`*

---

## Part B: VPS Setup (The Gateway)
The VPS acts purely as a traffic forwarder.

1.  **Install Nginx**:
    ```bash
    sudo apt update && sudo apt install -y nginx
    ```

2.  **Configure Nginx**:
    Edit `/etc/nginx/sites-available/default` (or your domain config):

    ```nginx
    server {
        listen 80;
        server_name music.yourdomain.com; # Replace with your domain

        location / {
            # Proxy to Mac Mini's Tailscale IP + Frontend Port (8090)
            # REPLACE 100.100.10.10 with your Mac's actual Tailscale IP
            proxy_pass http://100.100.10.10:8090;

            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            
            # WebSocket Support (for hot reload or future features)
            proxy_http_version 1.1;
            proxy_set_header Upgrade $http_upgrade;
            proxy_set_header Connection "upgrade";
        }
    }
    ```

3.  **Reload Nginx**:
    ```bash
    sudo systemctl reload nginx
    ```

## CORS & Cross-Origin
You asked about "Cross-Origin issues if Frontend is on VPS and Backend on Mac".
*   **In this solution**: There are **NO CORS issues**.
*   **Why**: The Frontend container (on Mac) contains Nginx which proxies `/api` to the Backend container (also on Mac). The VPS proxies *everything* to the Frontend container. To the browser, everything comes from `music.yourdomain.com`.

## Alternative: Cloudflare Tunnel
If Tailscale bandwidth is too unstable, an alternative is **Cloudflare Tunnel (cloudflared)** installed on the Mac. It exposes the Mac directly to a Cloudflare domain without needing the VPS or port forwarding.
