import { useState, useEffect } from 'react';
import { api } from '../services/api';
import { Music } from 'lucide-react';

interface SmbImageProps {
    smbPath: string | null | undefined;
    alt?: string;
    className?: string;
    fallbackClassName?: string;
}

/**
 * Component to display images from SMB paths with authentication.
 * Uses fetch with auth token to load the image as a blob.
 */
export default function SmbImage({ smbPath, alt = '', className = '', fallbackClassName = '' }: SmbImageProps) {
    const [blobUrl, setBlobUrl] = useState<string | null>(null);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState(false);

    useEffect(() => {
        if (!smbPath) {
            setBlobUrl(null);
            setError(false);
            return;
        }

        let isMounted = true;
        setLoading(true);
        setError(false);

        api.get(`/media/smb-image?path=${encodeURIComponent(smbPath)}`, {
            responseType: 'blob'
        })
            .then(response => {
                if (isMounted) {
                    const url = URL.createObjectURL(response.data);
                    setBlobUrl(url);
                    setError(false);
                }
            })
            .catch(err => {
                console.error('Failed to load SMB image:', err);
                if (isMounted) {
                    setError(true);
                }
            })
            .finally(() => {
                if (isMounted) {
                    setLoading(false);
                }
            });

        return () => {
            isMounted = false;
            if (blobUrl) {
                URL.revokeObjectURL(blobUrl);
            }
        };
    }, [smbPath]);

    // Cleanup on unmount
    useEffect(() => {
        return () => {
            if (blobUrl) {
                URL.revokeObjectURL(blobUrl);
            }
        };
    }, [blobUrl]);

    if (!smbPath || error) {
        return <Music size={48} className={fallbackClassName || "text-gray-700"} />;
    }

    if (loading || !blobUrl) {
        return <div className="animate-pulse bg-gray-700 rounded w-full h-full" />;
    }

    return (
        <img
            src={blobUrl}
            alt={alt}
            className={className}
        />
    );
}
