import { useState, useEffect } from 'react';
import { X, Plus, Music, FolderPlus } from 'lucide-react';
import { getPlaylists, addSongsToPlaylist, createPlaylist } from '../services/api';

interface AddToPlaylistModalProps {
    isOpen: boolean;
    onClose: () => void;
    songIds: number[]; // Support multiple songs
    defaultNewPlaylistName?: string;
}

interface SimplePlaylist {
    id: number;
    name: string;
    count: number;
}

export default function AddToPlaylistModal({ isOpen, onClose, songIds, defaultNewPlaylistName }: AddToPlaylistModalProps) {
    const [playlists, setPlaylists] = useState<SimplePlaylist[]>([]);
    const [loading, setLoading] = useState(true);
    const [showNewPlaylist, setShowNewPlaylist] = useState(false);
    const [newPlaylistName, setNewPlaylistName] = useState('');
    const [saving, setSaving] = useState(false);

    useEffect(() => {
        if (isOpen) {
            loadPlaylists();
            if (defaultNewPlaylistName) {
                setShowNewPlaylist(true);
                setNewPlaylistName(defaultNewPlaylistName);
            } else {
                setShowNewPlaylist(false);
                setNewPlaylistName('');
            }
        }
    }, [isOpen, defaultNewPlaylistName]);

    const loadPlaylists = async () => {
        try {
            setLoading(true);
            const res = await getPlaylists();
            setPlaylists(res.data);
        } catch (error) {
            console.error(error);
        } finally {
            setLoading(false);
        }
    };

    const handleSelect = async (playlistId: number) => {
        if (songIds.length === 0) return;
        try {
            setSaving(true);
            await addSongsToPlaylist(playlistId, songIds);
            alert(`Added ${songIds.length} song(s) to playlist!`);
            onClose();
        } catch (error) {
            alert("Failed to add songs.");
        } finally {
            setSaving(false);
        }
    };

    const handleCreateNew = async () => {
        if (!newPlaylistName.trim()) return;
        try {
            setSaving(true);
            // Create playlist first
            const res = await createPlaylist(newPlaylistName.trim());
            const newPlaylistId = res.data.id;

            // Add songs to new playlist
            if (songIds.length > 0) {
                await addSongsToPlaylist(newPlaylistId, songIds);
            }

            alert(`Created playlist "${newPlaylistName}" with ${songIds.length} song(s)!`);
            onClose();
        } catch (error) {
            alert("Failed to create playlist.");
        } finally {
            setSaving(false);
        }
    };

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/80 backdrop-blur-sm p-4">
            <div className="bg-gray-900 border border-gray-800 rounded-2xl w-full max-w-sm flex flex-col shadow-2xl animate-fade-in">
                <div className="flex items-center justify-between p-4 border-b border-gray-800">
                    <h3 className="font-bold flex items-center gap-2">
                        <Music size={18} className="text-blue-500" />
                        Add {songIds.length} Song{songIds.length > 1 ? 's' : ''} to Playlist
                    </h3>
                    <button onClick={onClose}><X size={20} className="text-gray-400 hover:text-white" /></button>
                </div>

                <div className="p-2 max-h-[60vh] overflow-y-auto">
                    {/* Create New Playlist Option */}
                    {!showNewPlaylist ? (
                        <button
                            onClick={() => setShowNewPlaylist(true)}
                            className="w-full text-left px-4 py-3 hover:bg-blue-600/10 rounded-lg flex items-center gap-3 transition border border-dashed border-gray-700 hover:border-blue-500 mb-2"
                        >
                            <div className="w-10 h-10 bg-blue-600/20 rounded flex items-center justify-center text-blue-500">
                                <FolderPlus size={18} />
                            </div>
                            <div>
                                <div className="font-medium text-blue-400">Create New Playlist</div>
                                <div className="text-xs text-gray-500">Add songs to a new playlist</div>
                            </div>
                        </button>
                    ) : (
                        <div className="px-4 py-3 bg-blue-600/10 rounded-lg border border-blue-500/30 mb-2">
                            <input
                                type="text"
                                placeholder="Enter playlist name..."
                                value={newPlaylistName}
                                onChange={(e) => setNewPlaylistName(e.target.value)}
                                className="w-full bg-black/30 border border-gray-700 rounded-lg px-3 py-2 text-sm focus:outline-none focus:border-blue-500 mb-2"
                                autoFocus
                            />
                            <div className="flex gap-2">
                                <button
                                    onClick={handleCreateNew}
                                    disabled={!newPlaylistName.trim() || saving}
                                    className="flex-1 px-3 py-2 bg-blue-600 hover:bg-blue-500 disabled:bg-gray-700 text-white text-sm rounded-lg font-medium transition"
                                >
                                    {saving ? 'Creating...' : 'Create & Add'}
                                </button>
                                <button
                                    onClick={() => setShowNewPlaylist(false)}
                                    className="px-3 py-2 bg-gray-800 hover:bg-gray-700 text-gray-300 text-sm rounded-lg transition"
                                >
                                    Cancel
                                </button>
                            </div>
                        </div>
                    )}

                    {/* Existing Playlists */}
                    {loading ? (
                        <div className="p-4 text-center text-gray-500 text-sm">Loading...</div>
                    ) : (
                        <div className="space-y-1">
                            {playlists.length === 0 && (
                                <div className="p-4 text-center text-gray-500 text-sm">No playlists yet.</div>
                            )}
                            {playlists.map(p => (
                                <button
                                    key={p.id}
                                    onClick={() => handleSelect(p.id)}
                                    disabled={saving}
                                    className="w-full text-left px-4 py-3 hover:bg-white/5 disabled:opacity-50 rounded-lg flex items-center gap-3 transition"
                                >
                                    <div className="w-10 h-10 bg-gray-800 rounded flex items-center justify-center text-gray-400">
                                        <Music size={18} />
                                    </div>
                                    <div>
                                        <div className="font-medium text-gray-200">{p.name}</div>
                                        <div className="text-xs text-gray-500">{p.count} songs</div>
                                    </div>
                                    <Plus size={16} className="ml-auto text-gray-500" />
                                </button>
                            ))}
                        </div>
                    )}
                </div>
            </div>
        </div>
    );
}
