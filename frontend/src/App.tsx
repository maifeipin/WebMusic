import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { AuthProvider, useAuth } from './context/AuthContext';
import { PlayerProvider } from './context/PlayerContext';
import Login from './pages/Login';
import Dashboard from './pages/Dashboard';
import Library from './pages/Library';
import Sources from './pages/Sources';
import FavoritesPage from './pages/FavoritesPage';
import HistoryPage from './pages/HistoryPage';
import { Layout } from './components/Layout';

const ProtectedRoute = ({ children }: { children: React.ReactElement }) => {
  const { isAuthenticated } = useAuth();
  return isAuthenticated ? children : <Navigate to="/login" />;
};

function App() {
  return (
    <AuthProvider>
      <PlayerProvider>
        <BrowserRouter>
          <div className="min-h-screen bg-black text-gray-200 font-sans">
            <Routes>
              <Route path="/login" element={<Login />} />
              <Route element={<ProtectedRoute><Layout /></ProtectedRoute>}>
                <Route path="/" element={<Dashboard />} />
                <Route path="/library" element={<Library />} />
                <Route path="/sources" element={<Sources />} />
                <Route path="/favorites" element={<FavoritesPage />} />
                <Route path="/history" element={<HistoryPage />} />
              </Route>
            </Routes>
          </div>
        </BrowserRouter>
      </PlayerProvider>
    </AuthProvider>
  );
}

export default App;
