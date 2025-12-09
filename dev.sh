#!/bin/bash

# Kill any existing processes on ports
echo "🧹 Cleaning up ports 5098 (backend) and 8090/5173 (frontend)..."
lsof -ti:5098 | xargs kill -9 2>/dev/null
lsof -ti:5173 | xargs kill -9 2>/dev/null
lsof -ti:8090 | xargs kill -9 2>/dev/null

echo "🚀 Starting WebMusic v2 Development Environment..."

# Start Backend in background
echo "backend..."
dotnet run --project backend/WebMusic.Backend.csproj --urls "http://0.0.0.0:5098" > backend.log 2>&1 &
BACKEND_PID=$!
echo "✅ Backend started (PID: $BACKEND_PID), logging to backend.log"

# Wait a bit for backend to warm up
sleep 3

# Start Frontend
echo "frontend..."
cd frontend
npm run dev &
FRONTEND_PID=$!
cd ..

echo "✅ Frontend started (PID: $FRONTEND_PID)"
echo ""
echo "🌟 App is running at: http://localhost:5173"
echo "📡 Backend API at:    http://localhost:5098"
echo ""
echo "Press Ctrl+C to stop all services."

# Trap Ctrl+C to kill both processes
trap "kill $BACKEND_PID $FRONTEND_PID; exit" SIGINT

# Keep script running
wait
