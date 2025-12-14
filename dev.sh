#!/bin/bash

# Get local IP address
LOCAL_IP=$(ipconfig getifaddr en0 2>/dev/null || hostname -I 2>/dev/null | awk '{print $1}')
if [ -z "$LOCAL_IP" ]; then
    LOCAL_IP="localhost"
fi

# Kill any existing processes on ports
# echo "🧹 Cleaning up ports 5098 (backend) and 5173 (frontend)..."
# lsof -n -ti:5098 | xargs kill -9 2>/dev/null
# lsof -n -ti:5173 | xargs kill -9 2>/dev/null
# lsof -n -ti:8090 | xargs kill -9 2>/dev/null

echo "🚀 Starting WebMusic v2 Development Environment..."

# Start Backend in background (listen on all interfaces)
echo "backend..."
dotnet run --project backend/WebMusic.Backend.csproj --urls "http://0.0.0.0:5098" > backend.log 2>&1 &
BACKEND_PID=$!
echo "✅ Backend started (PID: $BACKEND_PID), logging to backend.log"

# Wait a bit for backend to warm up
sleep 3

# Start Frontend (Vite now configured to listen on 0.0.0.0)
echo "frontend..."
cd frontend
npm run dev &
FRONTEND_PID=$!
cd ..

echo "✅ Frontend started (PID: $FRONTEND_PID)"
echo ""
echo "=================================================="
echo "🖥️  Desktop:  http://localhost:5173"
echo "📱 Mobile:   http://${LOCAL_IP}:5173"
echo "📡 API:      http://${LOCAL_IP}:5098"
echo "=================================================="
echo ""
echo "Press Ctrl+C to stop all services."

# Trap Ctrl+C to kill both processes
trap "kill $BACKEND_PID $FRONTEND_PID; exit" SIGINT

# Keep script running
wait
