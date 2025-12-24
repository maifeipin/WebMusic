import React, { useEffect, useState } from 'react';
import { getDuplicates, cleanupDuplicates } from '../services/api';
import { Trash2, AlertTriangle, FileAudio, Check, X, RefreshCw } from 'lucide-react';

interface DuplicateGroup {
    hash: string;
    sizeBytes: number;
    files: DuplicateFile[];
}

interface DuplicateFile {
    id: number;
    filePath: string;
    title?: string;
    artist?: string;
    album?: string;
    bitrate: number;
    scanSourceId: number;
    coverArt?: string;
    isFavorite: boolean;
    playlistCount: number;
    addedAt: string;
}

interface DuplicateCleanerModalProps {
    isOpen: boolean;
    onClose: () => void;
}

const formatSize = (bytes: number) => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

export const DuplicateCleanerModal: React.FC<DuplicateCleanerModalProps> = ({ isOpen, onClose }) => {
    const [loading, setLoading] = useState(false);
    const [groups, setGroups] = useState<DuplicateGroup[]>([]);
    const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set());
    const [status, setStatus] = useState<string>('');

    useEffect(() => {
        if (isOpen) {
            loadDuplicates();
        } else {
            setGroups([]);
            setSelectedIds(new Set());
            setStatus('');
        }
    }, [isOpen]);

    const loadDuplicates = async () => {
        setLoading(true);
        setStatus('Scanning for duplicates...');
        try {
            const res = await getDuplicates();
            setGroups(res.data);

            // Auto-select duplicates (Keep the FIRST one, delete others)
            // Backend sorts the best one to top.
            const newSelected = new Set<number>();
            res.data.forEach((group: any) => {
                const files = group.files || group.Files;
                if (files && files.length > 1) {
                    for (let i = 1; i < files.length; i++) {
                        newSelected.add(files[i].id);
                    }
                }
            });
            setSelectedIds(newSelected);
            setStatus(`Found ${res.data.length} groups of duplicates. Smart selected ${newSelected.size} files to delete.`);
        } catch (err: any) {
            console.error(err);
            if (err.response?.status === 403) {
                setStatus('Access Denied: Only Admin can clean duplicates.');
            } else {
                setStatus('Error loading duplicates.');
            }
        } finally {
            setLoading(false);
        }
    };

    const handleCleanup = async () => {
        if (selectedIds.size === 0) return;
        if (!confirm(`Are you sure you want to permanently delete ${selectedIds.size} files? This cannot be undone.`)) return;

        setLoading(true);
        setStatus('Deleting files...');
        try {
            const res = await cleanupDuplicates(Array.from(selectedIds));
            setStatus(`Deleted ${res.data.successCount} files. Failed: ${res.data.failedCount}`);
            // Refresh list
            loadDuplicates();
        } catch (err) {
            console.error(err);
            setStatus('Error deleting files.');
            setLoading(false);
        }
    };

    const toggleSelection = (id: number) => {
        const newSet = new Set(selectedIds);
        if (newSet.has(id)) {
            newSet.delete(id);
        } else {
            newSet.add(id);
        }
        setSelectedIds(newSet);
    };

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 backdrop-blur-sm p-4">
            <div className="bg-gray-900 border border-white/10 rounded-2xl w-full max-w-4xl max-h-[90vh] flex flex-col shadow-2xl">
                {/* Header */}
                <div className="p-6 border-b border-white/10 flex justify-between items-center bg-white/5 rounded-t-2xl">
                    <div className="flex items-center gap-3">
                        <div className="p-2 bg-orange-500/20 rounded-lg">
                            <AlertTriangle className="w-6 h-6 text-orange-400" />
                        </div>
                        <div>
                            <h2 className="text-xl font-bold text-white">Duplicate Cleaner</h2>
                            <p className="text-sm text-gray-400">Find and remove duplicate files to save space</p>
                        </div>
                    </div>
                    <button onClick={onClose} className="p-2 hover:bg-white/10 rounded-full transition-colors">
                        <X className="w-5 h-5 text-gray-400" />
                    </button>
                </div>

                {/* Content */}
                <div className="flex-1 overflow-y-auto p-6 space-y-6">
                    {loading && groups.length === 0 ? (
                        <div className="flex flex-col items-center justify-center py-20 text-gray-400 animate-pulse">
                            <RefreshCw className="w-12 h-12 mb-4 animate-spin" />
                            <p>Scanning library...</p>
                        </div>
                    ) : groups.length === 0 ? (
                        <div className="flex flex-col items-center justify-center py-20 text-gray-400">
                            <Check className="w-12 h-12 mb-4 text-green-500" />
                            <p className="text-lg">No duplicates found!</p>
                            <p className="text-sm opacity-60">Your library is clean.</p>
                        </div>
                    ) : (
                        <div className="space-y-6">
                            {/* Stats Bar */}
                            <div className="flex items-center justify-between bg-blue-500/10 border border-blue-500/20 rounded-xl p-4">
                                <div className="flex gap-6">
                                    <div>
                                        <div className="text-sm text-blue-300 mb-1">Total Duplicates</div>
                                        <div className="text-2xl font-bold text-blue-100">{groups.reduce((acc, g) => acc + (g.files?.length || 0) - 1, 0)} files</div>
                                    </div>
                                    <div>
                                        <div className="text-sm text-blue-300 mb-1">Wasted Space</div>
                                        <div className="text-2xl font-bold text-blue-100">
                                            {formatSize(groups.reduce((acc, g) => acc + (g.sizeBytes * ((g.files?.length || 0) - 1)), 0))}
                                        </div>
                                    </div>
                                </div>
                                <div className="text-right">
                                    <div className="text-sm text-blue-300 mb-1">Selected for Deletion</div>
                                    <div className="text-xl font-bold text-red-400">{selectedIds.size} files</div>
                                </div>
                            </div>

                            {groups.map((group, idx) => (
                                <div key={idx} className="bg-white/5 border border-white/10 rounded-xl overflow-hidden">
                                    <div className="bg-white/5 px-4 py-2 flex justify-between items-center text-sm border-b border-white/5">
                                        <span className="text-gray-400 font-mono text-xs truncate max-w-[300px]" title={group.hash}>Hash: {group.hash}</span>
                                        <span className="text-gray-300">{formatSize(group.sizeBytes)}</span>
                                    </div>
                                    <div className="divide-y divide-white/5">
                                        {group.files?.map((file) => {
                                            const isKept = !selectedIds.has(file.id);
                                            return (
                                                <div
                                                    key={file.id}
                                                    className={`flex items-center gap-4 p-4 transition-colors cursor-pointer ${selectedIds.has(file.id) ? 'bg-red-500/10 hover:bg-red-500/20' : 'bg-green-500/5 hover:bg-green-500/10'
                                                        }`}
                                                    onClick={() => toggleSelection(file.id)}
                                                >
                                                    <div className={`w-5 h-5 rounded border flex items-center justify-center transition-colors shrink-0 ${selectedIds.has(file.id)
                                                        ? 'bg-red-500 border-red-500'
                                                        : 'border-green-500 bg-green-500'
                                                        }`}>
                                                        {selectedIds.has(file.id) ? <Check className="w-3.5 h-3.5 text-white" /> : <Check className="w-3.5 h-3.5 text-white" />}
                                                    </div>

                                                    <div className="relative w-10 h-10 bg-gray-800 rounded-lg overflow-hidden shrink-0">
                                                        {file.coverArt ? (
                                                            <img src={file.coverArt} className="w-full h-full object-cover" />
                                                        ) : (
                                                            <div className="w-full h-full flex items-center justify-center">
                                                                <FileAudio className="w-5 h-5 text-gray-500" />
                                                            </div>
                                                        )}
                                                    </div>

                                                    <div className="flex-1 min-w-0">
                                                        <div className="font-medium text-white truncate flex items-center gap-2">
                                                            <span className={isKept ? 'text-green-400' : 'text-gray-300'}>{file.title || file.filePath.split(/[/\\]/).pop()}</span>
                                                            {isKept && <span className="text-[10px] px-1.5 py-0.5 rounded bg-green-500/20 text-green-400 font-bold uppercase tracking-wider">Keep</span>}
                                                            {selectedIds.has(file.id) && <span className="text-[10px] px-1.5 py-0.5 rounded bg-red-500/20 text-red-400 font-bold uppercase tracking-wider">Delete</span>}
                                                        </div>
                                                        <div className="text-sm text-gray-400 flex items-center gap-2 truncate">
                                                            {file.isFavorite && <span className="text-pink-500 text-xs flex items-center gap-0.5">♥ Favorite</span>}
                                                            {file.playlistCount > 0 && <span className="text-blue-400 text-xs flex items-center gap-0.5">≣ {file.playlistCount} Playlists</span>}
                                                            <span className="truncate opacity-50" title={file.filePath}>{file.filePath}</span>
                                                        </div>
                                                    </div>
                                                    <div className="text-xs text-gray-500 font-mono whitespace-nowrap">
                                                        {new Date(file.addedAt).toLocaleDateString()}
                                                    </div>
                                                </div>
                                            )
                                        })}
                                    </div>
                                </div>
                            ))}
                        </div>
                    )}
                </div>

                {/* Footer */}
                <div className="p-6 border-t border-white/10 bg-gray-900 rounded-b-2xl flex justify-between items-center">
                    <div className="text-sm text-gray-400">{status}</div>
                    <div className="flex gap-3">
                        <button
                            onClick={onClose}
                            className="px-4 py-2 bg-white/5 hover:bg-white/10 text-white rounded-lg transition-colors font-medium border border-white/10"
                        >
                            Cancel
                        </button>
                        <button
                            onClick={handleCleanup}
                            disabled={selectedIds.size === 0 || loading}
                            className={`px-6 py-2 rounded-lg flex items-center gap-2 font-medium transition-all ${selectedIds.size > 0 && !loading
                                ? 'bg-red-500 hover:bg-red-600 text-white shadow-lg shadow-red-500/20'
                                : 'bg-gray-800 text-gray-500 cursor-not-allowed'
                                }`}
                        >
                            {loading ? <RefreshCw className="w-4 h-4 animate-spin" /> : <Trash2 className="w-4 h-4" />}
                            Delete Selected ({selectedIds.size})
                        </button>
                    </div>
                </div>
            </div>
        </div>
    );
};
