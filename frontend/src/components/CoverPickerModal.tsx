import { useState, useEffect } from 'react';
import { X, ChevronRight, ChevronDown, FolderOpen, Image as ImageIcon, Check, RefreshCw } from 'lucide-react';
import { getCredentials, browse, api } from '../services/api';

interface Credential {
    id: number;
    name: string;
    host: string;
}

interface BrowsableItem {
    name: string;
    type: 'Share' | 'Directory' | 'File';
    path: string;
}

interface CoverPickerModalProps {
    isOpen: boolean;
    onClose: () => void;
    onSelect: (smbPath: string) => void;
    currentCover?: string;
}

const IMAGE_EXTENSIONS = ['.jpg', '.jpeg', '.png', '.webp', '.gif', '.bmp'];

function isImageFile(name: string): boolean {
    const lower = name.toLowerCase();
    return IMAGE_EXTENSIONS.some(ext => lower.endsWith(ext));
}

export default function CoverPickerModal({ isOpen, onClose, onSelect, currentCover }: CoverPickerModalProps) {
    const [credentials, setCredentials] = useState<Credential[]>([]);
    const [selectedCredential, setSelectedCredential] = useState<Credential | null>(null);
    const [currentPath, setCurrentPath] = useState('');
    const [items, setItems] = useState<BrowsableItem[]>([]);
    const [loading, setLoading] = useState(false);
    const [previewPath, setPreviewPath] = useState<string | null>(null);
    const [previewBlobUrl, setPreviewBlobUrl] = useState<string | null>(null);
    const [previewLoading, setPreviewLoading] = useState(false);
    const [previewError, setPreviewError] = useState(false);

    useEffect(() => {
        if (isOpen) {
            loadCredentials();
            setPreviewPath(currentCover || null);
            if (currentCover) {
                loadPreviewImage(currentCover);
            }
        }
        // Cleanup blob URL on unmount
        return () => {
            if (previewBlobUrl) {
                URL.revokeObjectURL(previewBlobUrl);
            }
        };
    }, [isOpen]);

    const loadPreviewImage = async (smbPath: string) => {
        setPreviewLoading(true);
        setPreviewError(false);

        // Revoke old blob URL
        if (previewBlobUrl) {
            URL.revokeObjectURL(previewBlobUrl);
            setPreviewBlobUrl(null);
        }

        try {
            const response = await api.get(`/media/smb-image?path=${encodeURIComponent(smbPath)}`, {
                responseType: 'blob'
            });
            const blobUrl = URL.createObjectURL(response.data);
            setPreviewBlobUrl(blobUrl);
            setPreviewError(false);
        } catch (e) {
            console.error('Failed to load preview:', e);
            setPreviewError(true);
        } finally {
            setPreviewLoading(false);
        }
    };

    const loadCredentials = async () => {
        try {
            const res = await getCredentials();
            setCredentials(res.data);
            if (res.data.length === 1) {
                // Auto-select if only one credential
                handleSelectCredential(res.data[0]);
            }
        } catch (e) {
            console.error(e);
        }
    };

    const handleSelectCredential = async (cred: Credential) => {
        setSelectedCredential(cred);
        setCurrentPath('');
        await loadPath(cred.id, '');
    };

    const loadPath = async (credentialId: number, path: string) => {
        setLoading(true);
        try {
            const res = await browse({ credentialId, path });
            // Filter to only show directories and image files
            const filtered = (res.data as BrowsableItem[]).filter(item =>
                item.type !== 'File' || isImageFile(item.name)
            );
            setItems(filtered);
            setCurrentPath(path);
        } catch (e) {
            console.error(e);
            setItems([]);
        } finally {
            setLoading(false);
        }
    };

    const handleItemClick = async (item: BrowsableItem) => {
        if (!selectedCredential) return;

        if (item.type === 'File') {
            // It's an image file - preview it
            // Clean up host - remove smb:// prefix if present
            let host = selectedCredential.host;
            if (host.startsWith('smb://')) {
                host = host.substring(6);
            }
            host = host.replace(/\\/g, '/').replace(/\/+$/, ''); // Remove trailing slashes

            const smbPath = `smb://${host}/${item.path}`;
            console.log('Preview SMB path:', smbPath); // Debug log
            setPreviewPath(smbPath);
            await loadPreviewImage(smbPath);
        } else {
            // It's a directory or share - navigate into it
            await loadPath(selectedCredential.id, item.path);
        }
    };

    const handleGoUp = async () => {
        if (!selectedCredential || !currentPath) return;
        const parts = currentPath.split('/').filter(Boolean);
        parts.pop();
        const newPath = parts.join('/');
        await loadPath(selectedCredential.id, newPath);
    };

    const handleApply = () => {
        if (previewPath) {
            onSelect(previewPath);
            onClose();
        }
    };

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 z-[70] flex items-center justify-center bg-black/80 backdrop-blur-sm p-4">
            <div className="bg-gray-900 border border-gray-800 rounded-2xl w-full max-w-4xl h-[85vh] flex flex-col shadow-2xl animate-fade-in">
                {/* Header */}
                <div className="flex items-center justify-between p-4 border-b border-gray-800">
                    <h2 className="text-lg font-bold">Select Cover Image</h2>
                    <button onClick={onClose} className="p-2 hover:bg-white/10 rounded-full transition">
                        <X size={20} />
                    </button>
                </div>

                {/* Body */}
                <div className="flex-1 flex overflow-hidden">
                    {/* Left: Browser */}
                    <div className="flex-1 flex flex-col border-r border-gray-800">
                        {/* Credential Selector */}
                        <div className="p-3 border-b border-gray-800">
                            <select
                                value={selectedCredential?.id || ''}
                                onChange={(e) => {
                                    const cred = credentials.find(c => c.id === Number(e.target.value));
                                    if (cred) handleSelectCredential(cred);
                                }}
                                className="w-full bg-black/50 border border-gray-700 rounded-lg px-3 py-2 text-sm focus:outline-none focus:border-blue-500"
                            >
                                <option value="">Select SMB Server...</option>
                                {credentials.map(c => (
                                    <option key={c.id} value={c.id}>{c.name} ({c.host})</option>
                                ))}
                            </select>
                        </div>

                        {/* Breadcrumb */}
                        {selectedCredential && (
                            <div className="px-3 py-2 text-xs text-gray-500 border-b border-gray-800 flex items-center gap-1 flex-wrap">
                                <button
                                    onClick={() => loadPath(selectedCredential.id, '')}
                                    className="hover:text-white transition"
                                >
                                    {selectedCredential.host}
                                </button>
                                {currentPath.split('/').filter(Boolean).map((part, i, arr) => (
                                    <span key={i} className="flex items-center gap-1">
                                        <ChevronRight size={12} />
                                        <button
                                            onClick={() => {
                                                const newPath = arr.slice(0, i + 1).join('/');
                                                loadPath(selectedCredential.id, newPath);
                                            }}
                                            className="hover:text-white transition"
                                        >
                                            {part}
                                        </button>
                                    </span>
                                ))}
                            </div>
                        )}

                        {/* File List */}
                        <div className="flex-1 overflow-y-auto p-2">
                            {loading ? (
                                <div className="p-4 text-center text-gray-500">
                                    <RefreshCw size={20} className="animate-spin mx-auto mb-2" />
                                    Loading...
                                </div>
                            ) : !selectedCredential ? (
                                <div className="p-4 text-center text-gray-500">
                                    Select an SMB server to browse
                                </div>
                            ) : items.length === 0 ? (
                                <div className="p-4 text-center text-gray-500">
                                    No images or folders found
                                </div>
                            ) : (
                                <div className="space-y-0.5">
                                    {currentPath && (
                                        <button
                                            onClick={handleGoUp}
                                            className="w-full text-left px-3 py-2 rounded-lg hover:bg-white/5 flex items-center gap-2 text-gray-400 text-sm"
                                        >
                                            <ChevronDown size={16} className="rotate-90" />
                                            <span>..</span>
                                        </button>
                                    )}
                                    {items.map((item, i) => (
                                        <button
                                            key={i}
                                            onClick={() => handleItemClick(item)}
                                            className={`w-full text-left px-3 py-2 rounded-lg hover:bg-white/5 flex items-center gap-2 text-sm transition ${previewPath?.endsWith('/' + item.path) ? 'bg-blue-500/20 text-blue-400' : 'text-gray-300'
                                                }`}
                                        >
                                            {item.type === 'File' ? (
                                                <ImageIcon size={16} className="text-green-500" />
                                            ) : (
                                                <FolderOpen size={16} className="text-yellow-500" />
                                            )}
                                            <span className="truncate">{item.name}</span>
                                        </button>
                                    ))}
                                </div>
                            )}
                        </div>
                    </div>

                    {/* Right: Preview */}
                    <div className="w-80 flex flex-col p-4">
                        <h3 className="text-sm font-medium text-gray-400 mb-3">Preview</h3>
                        <div className="flex-1 flex items-center justify-center bg-black/30 rounded-xl border border-gray-800 overflow-hidden">
                            {previewPath ? (
                                <div className="relative w-full h-full flex items-center justify-center">
                                    {previewLoading && (
                                        <div className="absolute inset-0 flex items-center justify-center bg-black/50 z-10">
                                            <RefreshCw size={24} className="animate-spin text-blue-500" />
                                        </div>
                                    )}
                                    {previewBlobUrl && !previewError && (
                                        <img
                                            src={previewBlobUrl}
                                            alt="Cover preview"
                                            className="max-w-full max-h-full object-contain"
                                        />
                                    )}
                                    {previewError && (
                                        <div className="absolute inset-0 flex items-center justify-center bg-black/80 text-red-400 text-sm">
                                            Failed to load image
                                        </div>
                                    )}
                                </div>
                            ) : (
                                <div className="text-gray-600 text-sm text-center p-4">
                                    <ImageIcon size={48} className="mx-auto mb-2 opacity-50" />
                                    Select an image to preview
                                </div>
                            )}
                        </div>

                        {previewPath && (
                            <div className="mt-3 text-xs text-gray-500 truncate" title={previewPath}>
                                {previewPath}
                            </div>
                        )}
                    </div>
                </div>

                {/* Footer */}
                <div className="p-4 border-t border-gray-800 flex justify-end gap-3">
                    <button
                        onClick={onClose}
                        className="px-4 py-2 text-gray-400 hover:text-white hover:bg-white/5 rounded-lg transition"
                    >
                        Cancel
                    </button>
                    <button
                        onClick={handleApply}
                        disabled={!previewPath || previewError}
                        className="px-4 py-2 bg-blue-600 hover:bg-blue-500 text-white rounded-lg flex items-center gap-2 transition disabled:opacity-50 disabled:cursor-not-allowed"
                    >
                        <Check size={18} />
                        Apply Cover
                    </button>
                </div>
            </div>
        </div>
    );
}
