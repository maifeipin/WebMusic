import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { AuthProvider, useAuth } from './context/AuthContext';
import { PlayerProvider } from './context/PlayerContext';
import Login from './pages/Login';
import Dashboard from './pages/Dashboard';
import Library from './pages/Library';
import Sources from './pages/Sources';
import FavoritesPage from './pages/FavoritesPage';
import HistoryPage from './pages/HistoryPage';
import PlaylistsPage from './pages/PlaylistsPage';
import PlaylistDetailPage from './pages/PlaylistDetailPage';
import BackupPage from './pages/BackupPage';
import TagManagerPage from './pages/TagManagerPage';
import SharedPlaylistPage from './pages/SharedPlaylistPage';
import AdminPage from './pages/AdminPage';
import PluginsPage from './pages/PluginsPage';
import PluginViewPage from './pages/PluginViewPage';
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
              {/* Public routes */}
              <Route path="/login" element={<Login />} />
              <Route path="/share/:token" element={<SharedPlaylistPage />} />

              {/* Protected routes */}
              <Route element={<ProtectedRoute><Layout /></ProtectedRoute>}>
                <Route path="/" element={<Dashboard />} />
                <Route path="/library" element={<Library />} />
                <Route path="/sources" element={<Sources />} />
                <Route path="/favorites" element={<FavoritesPage />} />
                <Route path="/history" element={<HistoryPage />} />
                <Route path="/playlists" element={<PlaylistsPage />} />
                <Route path="/playlists/:id" element={<PlaylistDetailPage />} />
                <Route path="/backup" element={<BackupPage />} />
                <Route path="/tags" element={<TagManagerPage />} />
                <Route path="/apps" element={<PluginsPage />} />
                <Route path="/apps/:id" element={<PluginViewPage />} />
                <Route path="/admin" element={<AdminPage />} />
              </Route>
            </Routes>
          </div>
        </BrowserRouter>
      </PlayerProvider>
    </AuthProvider>
  );
}

export default App;

