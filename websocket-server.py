#!/usr/bin/env python3
"""
Simple WebSocket server to receive screen mirror data from Android app.
Run this script, then connect your Android app to this computer's IP address.
"""

import asyncio
import websockets
import socket
from datetime import datetime
import os

# Configuration
PORT = 8765
HOST = "0.0.0.0"  # Listen on all interfaces

# Statistics
frame_count = 0
total_bytes = 0
clients = set()

def get_local_ip():
    """Get the local IP address"""
    try:
        # Create a socket to determine local IP
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            # Connect to a remote address (doesn't actually connect)
            s.connect(("8.8.8.8", 80))
            local_ip = s.getsockname()[0]
            return local_ip
    except Exception:
        return "localhost"

def log(message):
    """Log a message with timestamp"""
    timestamp = datetime.now().strftime("%H:%M:%S")
    print(f"[{timestamp}] {message}")

def save_frame(data, frame_num):
    """Save received frame as JPEG file for debugging"""
    try:
        # Create frames directory if it doesn't exist
        os.makedirs("frames", exist_ok=True)
        
        # Save the frame
        filename = f"frames/frame_{frame_num:04d}.jpg"
        with open(filename, "wb") as f:
            f.write(data)
        log(f"Saved frame to {filename}")
    except Exception as e:
        log(f"Error saving frame: {e}")

async def handle_client(websocket, path):
    """Handle a WebSocket client connection"""
    global frame_count, total_bytes
    
    client_address = websocket.remote_address
    log(f"New client connected: {client_address[0]}:{client_address[1]}")
    clients.add(websocket)
    
    try:
        async for message in websocket:
            if isinstance(message, str):
                # Text message
                log(f"Received text: {message}")
                
                # Send acknowledgment
                await websocket.send(f"Server received: {message}")
                
            elif isinstance(message, bytes):
                # Binary data (should be JPEG frames)
                frame_count += 1
                total_bytes += len(message)
                
                log(f"Received frame #{frame_count}: {len(message)} bytes "
                    f"(Total: {total_bytes:,} bytes)")
                
                # Save frame for debugging (optional - uncomment to save frames)
                # save_frame(message, frame_count)
                
                # Verify it's a JPEG by checking header
                if message.startswith(b'\xff\xd8\xff'):
                    log("✓ Valid JPEG header detected")
                else:
                    log("⚠ Warning: Data doesn't appear to be JPEG format")
                    log(f"First 20 bytes: {message[:20].hex()}")
                
    except websockets.exceptions.ConnectionClosed:
        log(f"Client {client_address[0]}:{client_address[1]} disconnected")
    except Exception as e:
        log(f"Error handling client {client_address[0]}:{client_address[1]}: {e}")
    finally:
        clients.discard(websocket)

async def start_server():
    """Start the WebSocket server"""
    local_ip = get_local_ip()
    
    log("=" * 60)
    log("Screen Mirror WebSocket Server")
    log("=" * 60)
    log(f"Starting server on {HOST}:{PORT}")
    log(f"Local IP address: {local_ip}")
    log(f"Android app should connect to: {local_ip}:{PORT}")
    log("=" * 60)
    
    # Start the server
    server = await websockets.serve(handle_client, HOST, PORT)
    log("✓ Server started successfully!")
    log("Waiting for Android app to connect...")
    log("Press Ctrl+C to stop the server")
    
    try:
        # Keep the server running
        await server.wait_closed()
    except KeyboardInterrupt:
        log("Server stopped by user")
    finally:
        log(f"Session summary:")
        log(f"  Total frames received: {frame_count}")
        log(f"  Total data received: {total_bytes:,} bytes")
        log("Server shutdown complete")

if __name__ == "__main__":
    try:
        # Run the server
        asyncio.run(start_server())
    except KeyboardInterrupt:
        print("\nServer stopped by user")
    except Exception as e:
        print(f"Server error: {e}")