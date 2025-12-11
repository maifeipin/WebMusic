import { useState } from 'react';
import { X, Save } from 'lucide-react';
import SelectionTree from './SelectionTree';
import { createPlaylist, addSongsToPlaylist } from '../services/api';

interface CreatePlaylistModalProps {
    isOpen: boolean;
    onClose: () => void;
    onSuccess: () => void;
}

export default function CreatePlaylistModal({ isOpen, onClose, onSuccess }: CreatePlaylistModalProps) {
    const [name, setName] = useState('');
    const [selectedIds, setSelectedIds] = useState<number[]>([]);
    const [saving, setSaving] = useState(false);

    if (!isOpen) return null;

    const handleSubmit = async () => {
        if (!name.trim()) return;
        setSaving(true);
        try {
            // 1. Create Playlist
            const res = await createPlaylist(name);
            const playlistId = res.data.id;

            // 2. Add Songs (if any)
            if (selectedIds.length > 0) {
                await addSongsToPlaylist(playlistId, selectedIds);
            }

            onSuccess();
            onClose();
            setName('');
            setSelectedIds([]);
        } catch (error) {
            console.error("Failed to create playlist", error);
            alert("Failed to create playlist.");
        } finally {
            setSaving(false);
        }
    };

    const toggleSelect = (id: number) => {
        setSelectedIds(prev =>
            prev.includes(id) ? prev.filter(x => x !== id) : [...prev, id]
        );
    };

    const handleBatchSelect = (ids: number[], select: boolean) => {
        setSelectedIds(prev => {
            if (select) {
                // Add all ids that are not already selected
                const newIds = ids.filter(id => !prev.includes(id));
                return [...prev, ...newIds];
            } else {
                // Remove all specified ids
                return prev.filter(id => !ids.includes(id));
            }
        });
    };

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 backdrop-blur-sm p-4">
            <div className="bg-gray-900 border border-gray-800 rounded-2xl w-full max-w-2xl h-[80vh] flex flex-col shadow-2xl animate-fade-in">

                {/* Header */}
                <div className="flex items-center justify-between p-6 border-b border-gray-800">
                    <h2 className="text-xl font-bold bg-gradient-to-r from-white to-gray-400 bg-clip-text text-transparent">
                        Create New Playlist
                    </h2>
                    <button onClick={onClose} className="p-2 hover:bg-white/10 rounded-full transition text-gray-400 hover:text-white">
                        <X size={20} />
                    </button>
                </div>

                {/* Body */}
                <div className="flex-1 overflow-hidden flex flex-col p-6 gap-6">
                    {/* Name Input */}
                    <div className="flex flex-col gap-2">
                        <label className="text-sm font-medium text-gray-400">Playlist Name</label>
                        <input
                            type="text"
                            value={name}
                            onChange={(e) => setName(e.target.value)}
                            placeholder="My Awesome Playlist"
                            className="bg-black/50 border border-gray-700 rounded-lg px-4 py-3 text-white focus:outline-none focus:border-blue-500 transition"
                            autoFocus
                        />
                        <p className="text-xs text-gray-500">
                            Tip: Click a folder to auto-fill the name. Expand folder and click checkbox to select all songs.
                        </p>
                    </div>

                    {/* Tree */}
                    <div className="flex-1 flex flex-col gap-2 min-h-0">
                        <div className="flex items-center justify-between">
                            <label className="text-sm font-medium text-gray-400">Select Songs ({selectedIds.length})</label>
                            {selectedIds.length > 0 && (
                                <button
                                    onClick={() => setSelectedIds([])}
                                    className="text-xs text-red-400 hover:text-red-300"
                                >
                                    Clear Selection
                                </button>
                            )}
                        </div>
                        <div className="flex-1 bg-black/30 border border-gray-800 rounded-lg p-2 overflow-hidden">
                            <SelectionTree
                                onNameSuggest={(n) => {
                                    if (!name) setName(n);
                                }}
                                selectedIds={selectedIds}
                                onToggleSelect={toggleSelect}
                                onBatchSelect={handleBatchSelect}
                            />
                        </div>
                    </div>
                </div>

                {/* Footer */}
                <div className="p-6 border-t border-gray-800 flex justify-end gap-3">
                    <button
                        onClick={onClose}
                        className="px-6 py-2 rounded-lg font-medium text-gray-400 hover:text-white hover:bg-white/5 transition"
                    >
                        Cancel
                    </button>
                    <button
                        onClick={handleSubmit}
                        disabled={!name.trim() || saving}
                        className="px-6 py-2 rounded-lg font-medium bg-blue-600 text-white hover:bg-blue-500 transition disabled:opacity-50 disabled:cursor-not-allowed flex items-center gap-2"
                    >
                        <Save size={18} />
                        {saving ? 'Creating...' : 'Create Playlist'}
                    </button>
                </div>

            </div>
        </div>
    );
}
