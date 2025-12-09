import { useState, useEffect } from 'react';
import { X, Plus, Music } from 'lucide-react';
import { getPlaylists, addSongsToPlaylist } from '../services/api';

interface AddToPlaylistModalProps {
    isOpen: boolean;
    onClose: () => void;
    songId: number | null;
}

interface SimplePlaylist {
    id: number;
    name: string;
    count: number;
}

export default function AddToPlaylistModal({ isOpen, onClose, songId }: AddToPlaylistModalProps) {
    const [playlists, setPlaylists] = useState<SimplePlaylist[]>([]);
    const [loading, setLoading] = useState(true);

    useEffect(() => {
        if (isOpen) loadPlaylists();
    }, [isOpen]);

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
        if (!songId) return;
        try {
            await addSongsToPlaylist(playlistId, [songId]);
            alert("Added to playlist!"); // Simple notification for now
            onClose();
        } catch (error) {
            alert("Failed to add song.");
        }
    };

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/80 backdrop-blur-sm p-4">
            <div className="bg-gray-900 border border-gray-800 rounded-2xl w-full max-w-sm flex flex-col shadow-2xl animate-fade-in">
                <div className="flex items-center justify-between p-4 border-b border-gray-800">
                    <h3 className="font-bold">Add to Playlist</h3>
                    <button onClick={onClose}><X size={20} className="text-gray-400 hover:text-white" /></button>
                </div>

                <div className="p-2 max-h-[60vh] overflow-y-auto">
                    {loading ? (
                        <div className="p-4 text-center text-gray-500 text-sm">Loading...</div>
                    ) : (
                        <div className="space-y-1">
                            {playlists.length === 0 && (
                                <div className="p-4 text-center text-gray-500 text-sm">No playlists found. Create one first.</div>
                            )}
                            {playlists.map(p => (
                                <button
                                    key={p.id}
                                    onClick={() => handleSelect(p.id)}
                                    className="w-full text-left px-4 py-3 hover:bg-white/5 rounded-lg flex items-center gap-3 transition"
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
